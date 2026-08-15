#import <Foundation/Foundation.h>
#import <CoreML/CoreML.h>
#import <Vision/Vision.h>
#import <CoreVideo/CoreVideo.h>
#import <ImageIO/CGImageProperties.h>
#include <float.h>
#include <math.h>
#include <stdint.h>

struct ARORYoloCandidate
{
    float x;
    float y;
    float width;
    float height;
    int classId;
    float confidence;
    int maskCoefficientCount;
    float maskCoefficients[32];
    bool hasMaskBottomCenter;
    float maskBottomCenterX;
    float maskBottomCenterY;
    bool hasMaskCenter;
    float maskCenterX;
    float maskCenterY;
};

@interface ARORYoloThresholdProvider : NSObject <MLFeatureProvider>
@property (nonatomic) double confidenceThreshold;
@property (nonatomic) double iouThreshold;
@end

@implementation ARORYoloThresholdProvider

- (NSSet<NSString *> *)featureNames
{
    return [NSSet setWithObjects:@"confidenceThreshold", @"iouThreshold", nil];
}

- (MLFeatureValue *)featureValueForName:(NSString *)featureName
{
    if ([featureName isEqualToString:@"confidenceThreshold"])
    {
        return [MLFeatureValue featureValueWithDouble:self.confidenceThreshold];
    }
    if ([featureName isEqualToString:@"iouThreshold"])
    {
        return [MLFeatureValue featureValueWithDouble:self.iouThreshold];
    }
    return nil;
}

@end

static VNCoreMLModel *gVisionModel = nil;
static ARORYoloThresholdProvider *gThresholdProvider = nil;
static BOOL gLoggedMissingModel = NO;
static int gYoloDebugLogCount = 0;

static float ARORClamp(float value, float minValue, float maxValue)
{
    return fmaxf(minValue, fminf(maxValue, value));
}

static float ARORSigmoid(float value)
{
    return 1.0f / (1.0f + expf(-value));
}

static float ARORIoU(const ARORYoloCandidate &left, const ARORYoloCandidate &right)
{
    const float leftX1 = left.x - left.width * 0.5f;
    const float leftY1 = left.y - left.height * 0.5f;
    const float leftX2 = left.x + left.width * 0.5f;
    const float leftY2 = left.y + left.height * 0.5f;
    const float rightX1 = right.x - right.width * 0.5f;
    const float rightY1 = right.y - right.height * 0.5f;
    const float rightX2 = right.x + right.width * 0.5f;
    const float rightY2 = right.y + right.height * 0.5f;

    const float intersectionX1 = fmaxf(leftX1, rightX1);
    const float intersectionY1 = fmaxf(leftY1, rightY1);
    const float intersectionX2 = fminf(leftX2, rightX2);
    const float intersectionY2 = fminf(leftY2, rightY2);
    const float intersectionWidth = fmaxf(0.0f, intersectionX2 - intersectionX1);
    const float intersectionHeight = fmaxf(0.0f, intersectionY2 - intersectionY1);
    const float intersectionArea = intersectionWidth * intersectionHeight;
    const float unionArea = left.width * left.height + right.width * right.height - intersectionArea;
    return unionArea > 0.0f ? intersectionArea / unionArea : 0.0f;
}

static BOOL ARORLoadYoloModel()
{
    if (gVisionModel != nil)
    {
        return YES;
    }

    NSBundle *bundle = [NSBundle mainBundle];
    NSURL *modelURL = [bundle URLForResource:@"yolo11n" withExtension:@"mlpackage"];
    if (modelURL == nil)
    {
        modelURL = [bundle URLForResource:@"yolov8n-seg" withExtension:@"mlpackage"];
    }
    if (modelURL == nil)
    {
        modelURL = [bundle URLForResource:@"yolov8n" withExtension:@"mlpackage"];
    }
    if (modelURL == nil)
    {
        NSString *streamingAssetsPath = [[[bundle resourcePath] stringByAppendingPathComponent:@"Data"]
            stringByAppendingPathComponent:@"Raw"];
        NSString *streamingModelPath = [streamingAssetsPath stringByAppendingPathComponent:@"yolo11n.mlpackage"];
        if ([[NSFileManager defaultManager] fileExistsAtPath:streamingModelPath])
        {
            modelURL = [NSURL fileURLWithPath:streamingModelPath isDirectory:YES];
        }
    }
    if (modelURL == nil)
    {
        NSString *streamingAssetsPath = [[[bundle resourcePath] stringByAppendingPathComponent:@"Data"]
            stringByAppendingPathComponent:@"Raw"];
        NSString *streamingModelPath = [streamingAssetsPath stringByAppendingPathComponent:@"yolov8n.mlpackage"];
        if ([[NSFileManager defaultManager] fileExistsAtPath:streamingModelPath])
        {
            modelURL = [NSURL fileURLWithPath:streamingModelPath isDirectory:YES];
        }
    }

    if (modelURL == nil)
    {
        modelURL = [bundle URLForResource:@"model" withExtension:@"mlmodel" subdirectory:@"yolo11n.mlpackage/Data/com.apple.CoreML"];
    }
    if (modelURL == nil)
    {
        modelURL = [bundle URLForResource:@"model" withExtension:@"mlmodel" subdirectory:@"yolov8n-seg.mlpackage/Data/com.apple.CoreML"];
    }
    if (modelURL == nil)
    {
        modelURL = [bundle URLForResource:@"model" withExtension:@"mlmodel" subdirectory:@"yolov8n.mlpackage/Data/com.apple.CoreML"];
    }
    if (modelURL == nil)
    {
        modelURL = [bundle URLForResource:@"model" withExtension:@"mlmodel" subdirectory:@"Data/Raw/yolo11n.mlpackage/Data/com.apple.CoreML"];
    }
    if (modelURL == nil)
    {
        modelURL = [bundle URLForResource:@"model" withExtension:@"mlmodel" subdirectory:@"Data/Raw/yolov8n-seg.mlpackage/Data/com.apple.CoreML"];
    }
    if (modelURL == nil)
    {
        modelURL = [bundle URLForResource:@"model" withExtension:@"mlmodel" subdirectory:@"Data/Raw/yolov8n.mlpackage/Data/com.apple.CoreML"];
    }

    if (modelURL == nil)
    {
        if (!gLoggedMissingModel)
        {
            NSLog(@"[M1 YOLO] yolo11n.mlpackage was not found. Checked app root and Data/Raw StreamingAssets.");
            gLoggedMissingModel = YES;
        }
        return NO;
    }

    NSError *error = nil;
    NSURL *compiledURL = nil;
    if ([[modelURL pathExtension] isEqualToString:@"mlpackage"] || [[modelURL pathExtension] isEqualToString:@"mlmodel"])
    {
        compiledURL = [MLModel compileModelAtURL:modelURL error:&error];
        if (compiledURL == nil || error != nil)
        {
            NSLog(@"[M1 YOLO] Failed to compile CoreML model: %@", error);
            return NO;
        }
    }
    else
    {
        compiledURL = modelURL;
    }

    MLModelConfiguration *configuration = [[MLModelConfiguration alloc] init];
    configuration.computeUnits = MLComputeUnitsAll;

    MLModel *model = [MLModel modelWithContentsOfURL:compiledURL configuration:configuration error:&error];
    if (model == nil || error != nil)
    {
        NSLog(@"[M1 YOLO] Failed to load CoreML model: %@", error);
        return NO;
    }

    gVisionModel = [VNCoreMLModel modelForMLModel:model error:&error];
    if (gVisionModel == nil || error != nil)
    {
        NSLog(@"[M1 YOLO] Failed to create Vision model: %@", error);
        return NO;
    }

    gVisionModel.inputImageFeatureName = @"image";
    gThresholdProvider = [[ARORYoloThresholdProvider alloc] init];
    gThresholdProvider.confidenceThreshold = 0.25;
    gThresholdProvider.iouThreshold = 0.45;
    gVisionModel.featureProvider = gThresholdProvider;

    NSLog(@"[M1 YOLO] CoreML YOLO loaded with MLComputeUnitsAll: %@", modelURL.lastPathComponent);
    return YES;
}

static CVPixelBufferRef ARORCreatePixelBufferFromRGBA(const uint8_t *rgbaBytes, int width, int height)
{
    NSDictionary *attributes = @{
        (NSString *)kCVPixelBufferCGImageCompatibilityKey: @YES,
        (NSString *)kCVPixelBufferCGBitmapContextCompatibilityKey: @YES
    };

    CVPixelBufferRef pixelBuffer = nil;
    CVReturn result = CVPixelBufferCreate(
        kCFAllocatorDefault,
        width,
        height,
        kCVPixelFormatType_32BGRA,
        (__bridge CFDictionaryRef)attributes,
        &pixelBuffer);

    if (result != kCVReturnSuccess || pixelBuffer == nil)
    {
        return nil;
    }

    CVPixelBufferLockBaseAddress(pixelBuffer, 0);
    uint8_t *destination = (uint8_t *)CVPixelBufferGetBaseAddress(pixelBuffer);
    const size_t destinationStride = CVPixelBufferGetBytesPerRow(pixelBuffer);
    for (int y = 0; y < height; y++)
    {
        uint8_t *row = destination + y * destinationStride;
        const uint8_t *source = rgbaBytes + y * width * 4;
        for (int x = 0; x < width; x++)
        {
            row[x * 4 + 0] = source[x * 4 + 2];
            row[x * 4 + 1] = source[x * 4 + 1];
            row[x * 4 + 2] = source[x * 4 + 0];
            row[x * 4 + 3] = source[x * 4 + 3];
        }
    }
    CVPixelBufferUnlockBaseAddress(pixelBuffer, 0);

    return pixelBuffer;
}

static NSArray<VNObservation *> *ARORRunVision(CVPixelBufferRef pixelBuffer, float confidenceThreshold, float iouThreshold)
{
    if (!ARORLoadYoloModel())
    {
        return nil;
    }

    gThresholdProvider.confidenceThreshold = confidenceThreshold;
    gThresholdProvider.iouThreshold = iouThreshold;

    __block NSArray<VNObservation *> *observations = nil;
    VNCoreMLRequest *request = [[VNCoreMLRequest alloc] initWithModel:gVisionModel completionHandler:^(VNRequest *request, NSError *error) {
        if (error != nil)
        {
            NSLog(@"[M1 YOLO] Vision request failed: %@", error);
            return;
        }

        observations = request.results;
    }];
    request.imageCropAndScaleOption = VNImageCropAndScaleOptionScaleFill;

    VNImageRequestHandler *handler = [[VNImageRequestHandler alloc] initWithCVPixelBuffer:pixelBuffer orientation:kCGImagePropertyOrientationRight options:@{}];
    NSError *error = nil;
    BOOL success = [handler performRequests:@[request] error:&error];
    if (!success || error != nil)
    {
        NSLog(@"[M1 YOLO] Vision handler failed: %@", error);
        return nil;
    }

    return observations;
}

static BOOL ARORFindMaskBottomCenter(
    MLMultiArray *prototypeArray,
    const ARORYoloCandidate &candidate,
    int imageWidth,
    int imageHeight,
    float *bottomCenterX,
    float *bottomCenterY,
    float *centerX,
    float *centerY)
{
    if (prototypeArray == nil ||
        bottomCenterX == nullptr ||
        bottomCenterY == nullptr ||
        centerX == nullptr ||
        centerY == nullptr ||
        candidate.maskCoefficientCount <= 0 ||
        imageWidth <= 0 ||
        imageHeight <= 0)
    {
        return NO;
    }

    NSArray<NSNumber *> *shape = prototypeArray.shape;
    int protoChannels = candidate.maskCoefficientCount;
    int protoHeight = 160;
    int protoWidth = 160;
    if (shape.count >= 4)
    {
        protoChannels = fminf(candidate.maskCoefficientCount, shape[shape.count - 3].intValue);
        protoHeight = shape[shape.count - 2].intValue;
        protoWidth = shape[shape.count - 1].intValue;
    }
    else if (shape.count >= 3)
    {
        protoChannels = fminf(candidate.maskCoefficientCount, shape[shape.count - 3].intValue);
        protoHeight = shape[shape.count - 2].intValue;
        protoWidth = shape[shape.count - 1].intValue;
    }

    if (protoChannels <= 0 || protoHeight <= 0 || protoWidth <= 0 ||
        prototypeArray.count < protoChannels * protoHeight * protoWidth)
    {
        return NO;
    }

    float *prototypeValues = (float *)prototypeArray.dataPointer;
    const float boxLeft = ARORClamp(candidate.x - candidate.width * 0.5f, 0.0f, (float)imageWidth);
    const float boxTop = ARORClamp(candidate.y - candidate.height * 0.5f, 0.0f, (float)imageHeight);
    const float boxRight = ARORClamp(candidate.x + candidate.width * 0.5f, 0.0f, (float)imageWidth);
    const float boxBottom = ARORClamp(candidate.y + candidate.height * 0.5f, 0.0f, (float)imageHeight);

    const int minX = (int)ARORClamp(floorf(boxLeft * protoWidth / imageWidth), 0.0f, (float)(protoWidth - 1));
    const int maxX = (int)ARORClamp(ceilf(boxRight * protoWidth / imageWidth), 0.0f, (float)(protoWidth - 1));
    const int minY = (int)ARORClamp(floorf(boxTop * protoHeight / imageHeight), 0.0f, (float)(protoHeight - 1));
    const int maxY = (int)ARORClamp(ceilf(boxBottom * protoHeight / imageHeight), 0.0f, (float)(protoHeight - 1));

    int bottomMaskY = -1;
    float maskSumX = 0.0f;
    float maskSumY = 0.0f;
    int maskCount = 0;
    for (int y = minY; y <= maxY; y++)
    {
        for (int x = minX; x <= maxX; x++)
        {
            float logit = 0.0f;
            const int pixelIndex = y * protoWidth + x;
            for (int channel = 0; channel < protoChannels; channel++)
            {
                logit += candidate.maskCoefficients[channel] *
                    prototypeValues[channel * protoHeight * protoWidth + pixelIndex];
            }

            if (ARORSigmoid(logit) >= 0.5f)
            {
                bottomMaskY = fmaxf(bottomMaskY, y);
                maskSumX += x;
                maskSumY += y;
                maskCount++;
            }
        }
    }

    if (bottomMaskY < 0 || maskCount <= 0)
    {
        return NO;
    }

    const int contactBandPixels = fmaxf(2, (maxY - minY + 1) * 0.08f);
    const int contactMinY = fmaxf(minY, bottomMaskY - contactBandPixels);
    float sumX = 0.0f;
    int count = 0;
    for (int y = contactMinY; y <= bottomMaskY; y++)
    {
        for (int x = minX; x <= maxX; x++)
        {
            float logit = 0.0f;
            const int pixelIndex = y * protoWidth + x;
            for (int channel = 0; channel < protoChannels; channel++)
            {
                logit += candidate.maskCoefficients[channel] *
                    prototypeValues[channel * protoHeight * protoWidth + pixelIndex];
            }

            if (ARORSigmoid(logit) >= 0.5f)
            {
                sumX += x;
                count++;
            }
        }
    }

    if (count <= 0)
    {
        return NO;
    }

    *bottomCenterX = (sumX / count + 0.5f) * imageWidth / protoWidth;
    *bottomCenterY = (bottomMaskY + 0.5f) * imageHeight / protoHeight;
    *centerX = (maskSumX / maskCount + 0.5f) * imageWidth / protoWidth;
    *centerY = (maskSumY / maskCount + 0.5f) * imageHeight / protoHeight;
    return YES;
}

static BOOL ARORParseYoloOutput(
    MLMultiArray *array,
    MLMultiArray *prototypeArray,
    int imageWidth,
    int imageHeight,
    int screenWidth,
    int screenHeight,
    float confidenceThreshold,
    float iouThreshold,
    ARORYoloCandidate *selected)
{
    const int predictionCount = 8400;
    if (array == nil || array.count < 84 * predictionCount || array.count % predictionCount != 0)
    {
        return NO;
    }

    const int channelCount = (int)(array.count / predictionCount);
    const int classCount = 80;
    const int maskCoefficientCount = prototypeArray != nil ? fminf(32, fmaxf(0, channelCount - 4 - classCount)) : 0;
    float *values = (float *)array.dataPointer;
    NSMutableArray<NSValue *> *candidates = [NSMutableArray array];

    for (int index = 0; index < predictionCount; index++)
    {
        float bestConfidence = 0.0f;
        int bestClass = -1;
        for (int classIndex = 0; classIndex < classCount && 4 + classIndex < channelCount; classIndex++)
        {
            const float confidence = values[(4 + classIndex) * predictionCount + index];
            if (confidence > bestConfidence)
            {
                bestConfidence = confidence;
                bestClass = classIndex;
            }
        }

        if (bestConfidence < confidenceThreshold)
        {
            continue;
        }

        ARORYoloCandidate candidate;
        candidate.x = values[index] * imageWidth / 640.0f;
        candidate.y = values[predictionCount + index] * imageHeight / 640.0f;
        candidate.width = values[2 * predictionCount + index] * imageWidth / 640.0f;
        candidate.height = values[3 * predictionCount + index] * imageHeight / 640.0f;
        candidate.classId = bestClass;
        candidate.confidence = bestConfidence;
        candidate.maskCoefficientCount = maskCoefficientCount;
        candidate.hasMaskBottomCenter = false;
        candidate.maskBottomCenterX = 0.0f;
        candidate.maskBottomCenterY = 0.0f;
        candidate.hasMaskCenter = false;
        candidate.maskCenterX = 0.0f;
        candidate.maskCenterY = 0.0f;
        for (int coeff = 0; coeff < maskCoefficientCount; coeff++)
        {
            candidate.maskCoefficients[coeff] = values[(4 + classCount + coeff) * predictionCount + index];
        }

        if (candidate.width <= 1.0f || candidate.height <= 1.0f)
        {
            continue;
        }

        [candidates addObject:[NSValue valueWithBytes:&candidate objCType:@encode(ARORYoloCandidate)]];
    }

    [candidates sortUsingComparator:^NSComparisonResult(NSValue *leftValue, NSValue *rightValue) {
        ARORYoloCandidate left;
        ARORYoloCandidate right;
        [leftValue getValue:&left];
        [rightValue getValue:&right];
        if (left.confidence > right.confidence)
        {
            return NSOrderedAscending;
        }
        if (left.confidence < right.confidence)
        {
            return NSOrderedDescending;
        }
        return NSOrderedSame;
    }];

    NSMutableArray<NSValue *> *kept = [NSMutableArray array];
    for (NSValue *value in candidates)
    {
        ARORYoloCandidate candidate;
        [value getValue:&candidate];

        BOOL suppressed = NO;
        for (NSValue *keptValue in kept)
        {
            ARORYoloCandidate keptCandidate;
            [keptValue getValue:&keptCandidate];
            if (ARORIoU(candidate, keptCandidate) > iouThreshold)
            {
                suppressed = YES;
                break;
            }
        }

        if (!suppressed)
        {
            [kept addObject:value];
            if (kept.count >= 20)
            {
                break;
            }
        }
    }

    const float centerX = imageWidth * 0.5f;
    const float centerY = imageHeight * 0.5f;
    BOOL found = NO;
    ARORYoloCandidate best;
    best.confidence = 0.0f;

    for (NSValue *value in kept)
    {
        ARORYoloCandidate candidate;
        [value getValue:&candidate];
        const float x1 = candidate.x - candidate.width * 0.5f;
        const float y1 = candidate.y - candidate.height * 0.5f;
        const float x2 = candidate.x + candidate.width * 0.5f;
        const float y2 = candidate.y + candidate.height * 0.5f;
        if (centerX >= x1 && centerX <= x2 && centerY >= y1 && centerY <= y2 && candidate.confidence > best.confidence)
        {
            best = candidate;
            found = YES;
        }
    }

    if (!found && kept.count > 0)
    {
        float bestDistance = FLT_MAX;
        for (NSValue *value in kept)
        {
            ARORYoloCandidate candidate;
            [value getValue:&candidate];
            const float dx = candidate.x - centerX;
            const float dy = candidate.y - centerY;
            const float distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
                found = YES;
            }
        }
    }

    if (!found)
    {
        return NO;
    }

    if (prototypeArray != nil && best.maskCoefficientCount > 0)
    {
        float maskX = 0.0f;
        float maskY = 0.0f;
        float centerX = 0.0f;
        float centerY = 0.0f;
        if (ARORFindMaskBottomCenter(prototypeArray, best, imageWidth, imageHeight, &maskX, &maskY, &centerX, &centerY))
        {
            best.hasMaskBottomCenter = true;
            best.maskBottomCenterX = ARORClamp(maskX * screenWidth / imageWidth, 0.0f, (float)screenWidth);
            best.maskBottomCenterY = ARORClamp(maskY * screenHeight / imageHeight, 0.0f, (float)screenHeight);
            best.hasMaskCenter = true;
            best.maskCenterX = ARORClamp(centerX * screenWidth / imageWidth, 0.0f, (float)screenWidth);
            best.maskCenterY = ARORClamp(centerY * screenHeight / imageHeight, 0.0f, (float)screenHeight);
        }
    }

    best.x = ARORClamp((best.x - best.width * 0.5f) * screenWidth / imageWidth, 0.0f, (float)screenWidth);
    best.y = ARORClamp((best.y - best.height * 0.5f) * screenHeight / imageHeight, 0.0f, (float)screenHeight);
    best.width = ARORClamp(best.width * screenWidth / imageWidth, 1.0f, (float)screenWidth - best.x);
    best.height = ARORClamp(best.height * screenHeight / imageHeight, 1.0f, (float)screenHeight - best.y);
    *selected = best;
    return YES;
}

static void ARORAddCandidateIfValid(
    NSMutableArray<NSValue *> *candidates,
    float centerX,
    float centerY,
    float width,
    float height,
    int classId,
    float confidence,
    int imageWidth,
    int imageHeight,
    float confidenceThreshold)
{
    if (confidence < confidenceThreshold || width <= 1.0f || height <= 1.0f)
    {
        return;
    }

    ARORYoloCandidate candidate;
    candidate.x = ARORClamp(centerX, 0.0f, (float)imageWidth);
    candidate.y = ARORClamp(centerY, 0.0f, (float)imageHeight);
    candidate.width = ARORClamp(width, 1.0f, (float)imageWidth);
    candidate.height = ARORClamp(height, 1.0f, (float)imageHeight);
    candidate.classId = classId;
    candidate.confidence = confidence;
    candidate.maskCoefficientCount = 0;
    candidate.hasMaskBottomCenter = false;
    candidate.maskBottomCenterX = 0.0f;
    candidate.maskBottomCenterY = 0.0f;
    candidate.hasMaskCenter = false;
    candidate.maskCenterX = 0.0f;
    candidate.maskCenterY = 0.0f;
    [candidates addObject:[NSValue valueWithBytes:&candidate objCType:@encode(ARORYoloCandidate)]];
}

static BOOL ARORSelectCenterCandidate(
    NSMutableArray<NSValue *> *candidates,
    int imageWidth,
    int imageHeight,
    int screenWidth,
    int screenHeight,
    float iouThreshold,
    ARORYoloCandidate *selected)
{
    if (candidates.count == 0 || selected == nullptr)
    {
        return NO;
    }

    [candidates sortUsingComparator:^NSComparisonResult(NSValue *leftValue, NSValue *rightValue) {
        ARORYoloCandidate left;
        ARORYoloCandidate right;
        [leftValue getValue:&left];
        [rightValue getValue:&right];
        if (left.confidence > right.confidence)
        {
            return NSOrderedAscending;
        }
        if (left.confidence < right.confidence)
        {
            return NSOrderedDescending;
        }
        return NSOrderedSame;
    }];

    NSMutableArray<NSValue *> *kept = [NSMutableArray array];
    for (NSValue *value in candidates)
    {
        ARORYoloCandidate candidate;
        [value getValue:&candidate];

        BOOL suppressed = NO;
        for (NSValue *keptValue in kept)
        {
            ARORYoloCandidate keptCandidate;
            [keptValue getValue:&keptCandidate];
            if (ARORIoU(candidate, keptCandidate) > iouThreshold)
            {
                suppressed = YES;
                break;
            }
        }

        if (!suppressed)
        {
            [kept addObject:value];
            if (kept.count >= 20)
            {
                break;
            }
        }
    }

    const float targetX = imageWidth * 0.5f;
    const float targetY = imageHeight * 0.5f;
    BOOL found = NO;
    ARORYoloCandidate best;
    best.confidence = 0.0f;

    for (NSValue *value in kept)
    {
        ARORYoloCandidate candidate;
        [value getValue:&candidate];
        const float x1 = candidate.x - candidate.width * 0.5f;
        const float y1 = candidate.y - candidate.height * 0.5f;
        const float x2 = candidate.x + candidate.width * 0.5f;
        const float y2 = candidate.y + candidate.height * 0.5f;
        if (targetX >= x1 && targetX <= x2 && targetY >= y1 && targetY <= y2 && candidate.confidence > best.confidence)
        {
            best = candidate;
            found = YES;
        }
    }

    if (!found)
    {
        float bestDistance = FLT_MAX;
        for (NSValue *value in kept)
        {
            ARORYoloCandidate candidate;
            [value getValue:&candidate];
            const float dx = candidate.x - targetX;
            const float dy = candidate.y - targetY;
            const float distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
                found = YES;
            }
        }
    }

    if (!found)
    {
        return NO;
    }

    best.x = ARORClamp((best.x - best.width * 0.5f) * screenWidth / imageWidth, 0.0f, (float)screenWidth);
    best.y = ARORClamp((best.y - best.height * 0.5f) * screenHeight / imageHeight, 0.0f, (float)screenHeight);
    best.width = ARORClamp(best.width * screenWidth / imageWidth, 1.0f, (float)screenWidth - best.x);
    best.height = ARORClamp(best.height * screenHeight / imageHeight, 1.0f, (float)screenHeight - best.y);
    *selected = best;
    return YES;
}

static BOOL ARORParseSplitYoloOutput(
    MLMultiArray *coordinatesArray,
    MLMultiArray *confidenceArray,
    int imageWidth,
    int imageHeight,
    int screenWidth,
    int screenHeight,
    float confidenceThreshold,
    float iouThreshold,
    ARORYoloCandidate *selected)
{
    const int coordinateCount = 4;
    const int classCount = 80;
    if (coordinatesArray == nil ||
        confidenceArray == nil ||
        coordinatesArray.count < coordinateCount ||
        confidenceArray.count < classCount ||
        coordinatesArray.count % coordinateCount != 0 ||
        confidenceArray.count % classCount != 0)
    {
        return NO;
    }

    const int coordinatePredictionCount = (int)(coordinatesArray.count / coordinateCount);
    const int confidencePredictionCount = (int)(confidenceArray.count / classCount);
    const int predictionCount = fminf(coordinatePredictionCount, confidencePredictionCount);
    if (predictionCount <= 0)
    {
        if (gYoloDebugLogCount < 20)
        {
            NSLog(@"[M1 YOLO] split output empty coordinates_count=%ld confidence_count=%ld",
                (long)coordinatesArray.count,
                (long)confidenceArray.count);
            gYoloDebugLogCount++;
        }
        return NO;
    }

    float *coordinates = (float *)coordinatesArray.dataPointer;
    float *confidences = (float *)confidenceArray.dataPointer;
    NSMutableArray<NSValue *> *candidates = [NSMutableArray array];
    float maxConfidenceSeen = 0.0f;

    for (int index = 0; index < predictionCount; index++)
    {
        float bestConfidence = 0.0f;
        int bestClass = -1;
        for (int classIndex = 0; classIndex < classCount; classIndex++)
        {
            const float confidence = confidences[index * classCount + classIndex];
            if (confidence > bestConfidence)
            {
                bestConfidence = confidence;
                bestClass = classIndex;
            }
        }
        maxConfidenceSeen = fmaxf(maxConfidenceSeen, bestConfidence);

        float x = coordinates[index * coordinateCount + 0];
        float y = coordinates[index * coordinateCount + 1];
        float width = coordinates[index * coordinateCount + 2];
        float height = coordinates[index * coordinateCount + 3];

        if (fabsf(x) <= 2.0f && fabsf(y) <= 2.0f && fabsf(width) <= 2.0f && fabsf(height) <= 2.0f)
        {
            x *= imageWidth;
            y *= imageHeight;
            width *= imageWidth;
            height *= imageHeight;
        }
        else
        {
            x = x * imageWidth / 640.0f;
            y = y * imageHeight / 640.0f;
            width = width * imageWidth / 640.0f;
            height = height * imageHeight / 640.0f;
        }

        ARORAddCandidateIfValid(
            candidates,
            x,
            y,
            width,
            height,
            bestClass,
            bestConfidence,
            imageWidth,
            imageHeight,
            confidenceThreshold);
    }

    if (gYoloDebugLogCount < 20)
    {
        NSLog(@"[M1 YOLO] split output coordinates_count=%ld confidence_count=%ld predictions=%d candidates=%lu max_conf=%.4f threshold=%.3f",
            (long)coordinatesArray.count,
            (long)confidenceArray.count,
            predictionCount,
            (unsigned long)candidates.count,
            maxConfidenceSeen,
            confidenceThreshold);
        if (predictionCount > 0)
        {
            NSLog(@"[M1 YOLO] split first coord=(%.3f, %.3f, %.3f, %.3f)",
                coordinates[0],
                coordinates[1],
                coordinates[2],
                coordinates[3]);
        }
        gYoloDebugLogCount++;
    }

    return ARORSelectCenterCandidate(candidates, imageWidth, imageHeight, screenWidth, screenHeight, iouThreshold, selected);
}

static int ARORClassIdForLabel(NSString *label)
{
    static NSDictionary<NSString *, NSNumber *> *labelToId = nil;
    if (labelToId == nil)
    {
        labelToId = @{
            @"person": @0, @"bicycle": @1, @"car": @2, @"motorcycle": @3,
            @"airplane": @4, @"bus": @5, @"train": @6, @"truck": @7,
            @"boat": @8, @"traffic light": @9, @"fire hydrant": @10,
            @"stop sign": @11, @"parking meter": @12, @"bench": @13,
            @"bird": @14, @"cat": @15, @"dog": @16, @"horse": @17,
            @"sheep": @18, @"cow": @19, @"elephant": @20, @"bear": @21,
            @"zebra": @22, @"giraffe": @23, @"backpack": @24,
            @"umbrella": @25, @"handbag": @26, @"tie": @27,
            @"suitcase": @28, @"frisbee": @29, @"skis": @30,
            @"snowboard": @31, @"sports ball": @32, @"kite": @33,
            @"baseball bat": @34, @"baseball glove": @35, @"skateboard": @36,
            @"surfboard": @37, @"tennis racket": @38, @"bottle": @39,
            @"wine glass": @40, @"cup": @41, @"fork": @42, @"knife": @43,
            @"spoon": @44, @"bowl": @45, @"banana": @46, @"apple": @47,
            @"sandwich": @48, @"orange": @49, @"broccoli": @50,
            @"carrot": @51, @"hot dog": @52, @"pizza": @53, @"donut": @54,
            @"cake": @55, @"chair": @56, @"couch": @57,
            @"potted plant": @58, @"bed": @59, @"dining table": @60,
            @"toilet": @61, @"tv": @62, @"laptop": @63, @"mouse": @64,
            @"remote": @65, @"keyboard": @66, @"cell phone": @67,
            @"microwave": @68, @"oven": @69, @"toaster": @70, @"sink": @71,
            @"refrigerator": @72, @"book": @73, @"clock": @74, @"vase": @75,
            @"scissors": @76, @"teddy bear": @77, @"hair drier": @78,
            @"toothbrush": @79
        };
    }

    NSNumber *classId = labelToId[label ?: @""];
    return classId != nil ? classId.intValue : -1;
}

static BOOL ARORParseRecognizedObjects(
    NSArray<VNRecognizedObjectObservation *> *recognizedObjects,
    int imageWidth,
    int imageHeight,
    int screenWidth,
    int screenHeight,
    float confidenceThreshold,
    float iouThreshold,
    ARORYoloCandidate *selected)
{
    if (recognizedObjects.count == 0)
    {
        return NO;
    }

    NSMutableArray<NSValue *> *candidates = [NSMutableArray array];
    float maxConfidenceSeen = 0.0f;

    for (VNRecognizedObjectObservation *object in recognizedObjects)
    {
        VNClassificationObservation *label = object.labels.firstObject;
        float confidence = label != nil ? label.confidence : object.confidence;
        maxConfidenceSeen = fmaxf(maxConfidenceSeen, confidence);

        CGRect box = object.boundingBox;
        float width = box.size.width * imageWidth;
        float height = box.size.height * imageHeight;
        float centerX = (box.origin.x + box.size.width * 0.5f) * imageWidth;
        float topY = (1.0f - box.origin.y - box.size.height) * imageHeight;
        float centerY = topY + height * 0.5f;

        ARORAddCandidateIfValid(
            candidates,
            centerX,
            centerY,
            width,
            height,
            ARORClassIdForLabel(label.identifier),
            confidence,
            imageWidth,
            imageHeight,
            confidenceThreshold);
    }

    if (gYoloDebugLogCount < 20)
    {
        NSLog(@"[M1 YOLO] recognized objects=%lu candidates=%lu max_conf=%.4f threshold=%.3f",
            (unsigned long)recognizedObjects.count,
            (unsigned long)candidates.count,
            maxConfidenceSeen,
            confidenceThreshold);
        if (recognizedObjects.count > 0)
        {
            VNRecognizedObjectObservation *first = recognizedObjects.firstObject;
            VNClassificationObservation *firstLabel = first.labels.firstObject;
            NSLog(@"[M1 YOLO] first recognized label=%@ conf=%.4f bbox=(%.3f, %.3f, %.3f, %.3f)",
                firstLabel.identifier,
                firstLabel != nil ? firstLabel.confidence : first.confidence,
                first.boundingBox.origin.x,
                first.boundingBox.origin.y,
                first.boundingBox.size.width,
                first.boundingBox.size.height);
        }
        gYoloDebugLogCount++;
    }

    return ARORSelectCenterCandidate(candidates, imageWidth, imageHeight, screenWidth, screenHeight, iouThreshold, selected);
}

extern "C"
{
    bool AROR_YoloIsAvailable()
    {
        return ARORLoadYoloModel();
    }

    bool AROR_YoloDetectCenterObject(
        const uint8_t *rgbaBytes,
        int byteCount,
        int imageWidth,
        int imageHeight,
        int screenWidth,
        int screenHeight,
        float confidenceThreshold,
        float iouThreshold,
        float *x,
        float *y,
        float *width,
        float *height,
        int *classId,
        float *confidence,
        int *hasMaskBottomCenter,
        float *maskBottomCenterX,
        float *maskBottomCenterY,
        int *hasMaskCenter,
        float *maskCenterX,
        float *maskCenterY)
    {
        if (rgbaBytes == nullptr || byteCount < imageWidth * imageHeight * 4 || imageWidth <= 0 || imageHeight <= 0)
        {
            return false;
        }

        CVPixelBufferRef pixelBuffer = ARORCreatePixelBufferFromRGBA(rgbaBytes, imageWidth, imageHeight);
        if (pixelBuffer == nil)
        {
            return false;
        }

        NSArray<VNObservation *> *observations = ARORRunVision(pixelBuffer, confidenceThreshold, iouThreshold);
        CVPixelBufferRelease(pixelBuffer);
        if (observations.count == 0)
        {
            if (gYoloDebugLogCount < 20)
            {
                NSLog(@"[M1 YOLO] Vision returned no observations");
                gYoloDebugLogCount++;
            }
            return false;
        }

        MLMultiArray *multiArray = nil;
        MLMultiArray *coordinatesArray = nil;
        MLMultiArray *confidenceArray = nil;
        MLMultiArray *prototypeArray = nil;
        NSMutableArray<VNRecognizedObjectObservation *> *recognizedObjects = [NSMutableArray array];
        for (VNObservation *genericObservation in observations)
        {
            if ([genericObservation isKindOfClass:[VNRecognizedObjectObservation class]])
            {
                [recognizedObjects addObject:(VNRecognizedObjectObservation *)genericObservation];
                if (gYoloDebugLogCount < 20)
                {
                    VNRecognizedObjectObservation *object = (VNRecognizedObjectObservation *)genericObservation;
                    VNClassificationObservation *label = object.labels.firstObject;
                    NSLog(@"[M1 YOLO] observation recognized label=%@ conf=%.4f bbox=(%.3f, %.3f, %.3f, %.3f)",
                        label.identifier,
                        label != nil ? label.confidence : object.confidence,
                        object.boundingBox.origin.x,
                        object.boundingBox.origin.y,
                        object.boundingBox.size.width,
                        object.boundingBox.size.height);
                }
                continue;
            }

            if (![genericObservation isKindOfClass:[VNCoreMLFeatureValueObservation class]])
            {
                if (gYoloDebugLogCount < 20)
                {
                    NSLog(@"[M1 YOLO] observation class=%@ ignored", NSStringFromClass([genericObservation class]));
                }
                continue;
            }

            VNCoreMLFeatureValueObservation *observation = (VNCoreMLFeatureValueObservation *)genericObservation;
            MLMultiArray *candidateArray = observation.featureValue.multiArrayValue;
            if (candidateArray == nil)
            {
                if (gYoloDebugLogCount < 20)
                {
                    NSLog(@"[M1 YOLO] observation %@ has no multiArray", observation.featureName);
                    gYoloDebugLogCount++;
                }
                continue;
            }

            NSString *featureName = observation.featureName;
            if (gYoloDebugLogCount < 20)
            {
                NSLog(@"[M1 YOLO] observation feature=%@ count=%ld shape=%@",
                    featureName,
                    (long)candidateArray.count,
                    candidateArray.shape);
            }
            if ([featureName isEqualToString:@"coordinates"] && candidateArray.count % 4 == 0)
            {
                coordinatesArray = candidateArray;
            }
            else if ([featureName isEqualToString:@"confidence"] && candidateArray.count % 80 == 0)
            {
                confidenceArray = candidateArray;
            }
            else if (candidateArray.count >= 84 * 8400 && candidateArray.count % 8400 == 0)
            {
                multiArray = candidateArray;
            }
            else if (candidateArray.count >= 32 * 160 * 160)
            {
                prototypeArray = candidateArray;
            }
        }

        ARORYoloCandidate selected;
        BOOL parsed = NO;
        if (recognizedObjects.count > 0)
        {
            parsed = ARORParseRecognizedObjects(
                recognizedObjects,
                imageWidth,
                imageHeight,
                screenWidth,
                screenHeight,
                confidenceThreshold,
                iouThreshold,
                &selected);
        }
        else if (coordinatesArray != nil && confidenceArray != nil)
        {
            parsed = ARORParseSplitYoloOutput(
                coordinatesArray,
                confidenceArray,
                imageWidth,
                imageHeight,
                screenWidth,
                screenHeight,
                confidenceThreshold,
                iouThreshold,
                &selected);
        }
        else
        {
            parsed = ARORParseYoloOutput(
                multiArray,
                prototypeArray,
                imageWidth,
                imageHeight,
                screenWidth,
                screenHeight,
                confidenceThreshold,
                iouThreshold,
                &selected);
        }

        if (!parsed)
        {
            if (gYoloDebugLogCount < 20)
            {
                NSLog(@"[M1 YOLO] parse failed has_split=%d has_raw=%d has_proto=%d",
                    coordinatesArray != nil && confidenceArray != nil,
                    multiArray != nil,
                    prototypeArray != nil);
                gYoloDebugLogCount++;
            }
            return false;
        }

        *x = selected.x;
        *y = selected.y;
        *width = selected.width;
        *height = selected.height;
        *classId = selected.classId;
        *confidence = selected.confidence;
        *hasMaskBottomCenter = selected.hasMaskBottomCenter ? 1 : 0;
        *maskBottomCenterX = selected.maskBottomCenterX;
        *maskBottomCenterY = selected.maskBottomCenterY;
        *hasMaskCenter = selected.hasMaskCenter ? 1 : 0;
        *maskCenterX = selected.maskCenterX;
        *maskCenterY = selected.maskCenterY;
        return true;
    }
}
