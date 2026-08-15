# Old YOLO Implementation Notes

This document preserves the important implementation details from `旧版/AR-Object-Replacement-System` so the old folder can be deleted later without losing context.

## Architecture

The old project had two YOLO paths:

- Python path: used for local development, webcam demos, evaluation, and algorithm validation.
- iPhone runtime path: used inside the Unity iOS app through CoreML + Vision + an Objective-C++ plugin.

The current FoundationPose iPhone sender should use the same runtime split:

- iPhone runs ARKit capture and YOLO.
- iPhone converts the selected YOLO center-object bbox into the FoundationPose registration mask.
- Host machine runs only FoundationPose and receives RGB/depth/mask frames.

Python YOLO is not the iPhone runtime. It is the reference implementation for model behavior, thresholds, NMS, and center-object selection.

## Old Python Detection Path

Important files:

- `detection/config.py`
- `detection/service.py`
- `detection/postprocess.py`
- `detection/types.py`
- `detection/model_manager.py`
- `detection/device.py`
- `config/detection.yaml`
- `app/detection_demo.py`

Core behavior:

- `DetectionConfig.from_yaml()` loads `config/detection.yaml`.
- `UltralyticsPredictor` loads `YOLO(str(model_path), task="detect")`.
- Inference calls `model.predict(frame, imgsz=640, device=..., half=...)`.
- Raw detections come from Ultralytics `result.boxes`.
- Boxes are read as pixel `xyxy` coordinates.
- Each detection becomes a `RawPrediction(class_id, confidence, BoundingBox(x1, y1, x2, y2))`.
- `DetectionService.detect(frame)` clips boxes to the frame.
- Then it runs custom NMS from `postprocess.py`.
- If `center_only` is enabled, it runs `filter_center_object()`.

Default old config values:

- `confidence_threshold: 0.25`
- `nms_iou_threshold: 0.45`
- `max_detections: 20`
- `image_size: 640`
- `center_detection.enabled: true`
- `center_detection.region_ratio: 0.25`
- `center_detection.require_center_inside_bbox: true`

Python center-object selection:

1. Compute frame center: `(width * 0.5, height * 0.5)`.
2. If `require_center_inside_bbox` and a bbox contains the center, give it score `1.0`.
3. Otherwise compute distance from bbox center to frame center.
4. If distance is outside `min(width, height) * center_region_ratio`, score is `0`.
5. Otherwise score is `1 - distance / max_distance`.
6. Keep candidates with score greater than `0`.
7. Sort by `(center_score, confidence)` descending.
8. Return only the top detection.

The Python path is useful for sanity-checking model quality because it relies on Ultralytics postprocessing rather than a custom CoreML parser.

## Old iPhone Runtime Path

Important files:

- `Assets/Scripts/Detection/YoloCoreMLDetector.cs`
- `Assets/Scripts/Detection/DetectionResult.cs`
- `Assets/Scripts/Detection/BoundingBoxMapper.cs`
- `Assets/Scripts/Detection/CocoClassNames.cs`
- `Assets/Plugins/iOS/YoloCoreMLPlugin.mm`
- `Assets/Scripts/Demo/PointCloudCaptureDemo.cs`
- `Assets/Editor/IOSFileSharingPostprocessor.cs`

Unity C# wrapper:

- `YoloCoreMLDetector.IsAvailable` calls native `AROR_YoloIsAvailable()`.
- `TryDetectCenterObject(byte[] rgbaBytes, int imageWidth, int imageHeight, float confidenceThreshold, float iouThreshold, out DetectionResult result)` calls native `AROR_YoloDetectCenterObject(...)`.
- It is active only under `UNITY_IOS && !UNITY_EDITOR`.
- It passes `Screen.width` and `Screen.height` into the native plugin.
- It returns a screen-space `Rect PixelRect`, `ClassId`, `Confidence`, timestamp, optional mask center and optional mask bottom center.

Old demo camera conversion:

- `ARCameraManager.TryAcquireLatestCpuImage(out cameraImage)`.
- Convert the full image:
  - `inputRect = full camera image`
  - `outputDimensions = yoloInputWidth/yoloInputHeight`
  - old defaults: `640 x 480`
  - `outputFormat = TextureFormat.RGBA32`
  - `transformation = XRCpuImage.Transformation.None`
- Pass the RGBA bytes to `YoloCoreMLDetector.TryDetectCenterObject(...)`.
- Thresholds used by the demo:
  - confidence `0.25`
  - IoU `0.45`

Old overlay:

- Detection box uses `detection.PixelRect`.
- UI anchored position:
  - `x = rect.xMin`
  - `y = Screen.height - rect.yMax`
- Label uses `YOLO {CocoClassNames.GetName(classId)} {confidence}`.

## Old Objective-C++ CoreML Plugin

Important exported functions:

- `AROR_YoloIsAvailable()`
- `AROR_YoloDetectCenterObject(...)`

Model loading:

- Old plugin looked for:
  - `yolov8n-seg.mlpackage`
  - `yolov8n.mlpackage`
  - same names under `Data/Raw`
  - nested `model.mlmodel` inside `.mlpackage/Data/com.apple.CoreML`
- For the current project, this should be adapted to prefer `yolo11n.mlpackage`.
- The plugin compiles `.mlpackage` or `.mlmodel` with `MLModel compileModelAtURL`.
- It loads with `MLComputeUnitsAll`.
- It wraps the model as `VNCoreMLModel`.

Pixel buffer conversion:

- Unity sends RGBA32 bytes.
- Plugin creates a `kCVPixelFormatType_32BGRA` `CVPixelBuffer`.
- It converts RGBA to BGRA row by row.

Vision request:

- Uses `VNCoreMLRequest`.
- `imageCropAndScaleOption = VNImageCropAndScaleOptionScaleFill`.
- Old plugin uses `VNImageRequestHandler` orientation `kCGImagePropertyOrientationRight`.
- It collects `VNCoreMLFeatureValueObservation`.

Old raw YOLO parsing:

- Expects one `MLMultiArray` with raw YOLO output.
- Old hardcoded assumption:
  - `predictionCount = 8400`
  - minimum channels: `84`
  - `classCount = 80`
- Layout:
  - channel 0: center x
  - channel 1: center y
  - channel 2: width
  - channel 3: height
  - channels 4..83: class confidences
  - optional mask coefficients after class channels when using a segmentation model
- Converts model-space coordinates to image-space with:
  - `x * imageWidth / 640`
  - `y * imageHeight / 640`
  - `width * imageWidth / 640`
  - `height * imageHeight / 640`
- Drops detections below threshold.
- Drops boxes with width/height <= 1.
- Sorts candidates by confidence descending.
- Runs IoU suppression using the provided IoU threshold.
- Keeps at most 20 boxes.

Old native center-object selection:

1. Compute image center: `(imageWidth * 0.5, imageHeight * 0.5)`.
2. Among NMS-kept boxes, first choose the highest-confidence box whose bbox contains image center.
3. If no box contains the center, choose the kept box whose center is closest to the image center.
4. Convert selected center-format bbox into screen-space top-left rect:
   - `x = (centerX - width * 0.5) * screenWidth / imageWidth`
   - `y = (centerY - height * 0.5) * screenHeight / imageHeight`
   - `width = width * screenWidth / imageWidth`
   - `height = height * screenHeight / imageHeight`
5. Clamp the result to screen bounds.

Segmentation support in old plugin:

- If a prototype mask array exists and mask coefficients are available, old plugin estimates:
  - mask center
  - mask bottom center
- This was used for the old point-cloud/replacement workflow.
- FoundationPose currently needs a bbox-derived binary mask, so the optional mask center is not required for registration.

## Current FoundationPose Adaptation

Current project files related to the adapted iPhone YOLO path:

- `Assets/Scripts/Detection/YoloCoreMLDetector.cs`
- `Assets/Scripts/Detection/DetectionResult.cs`
- `Assets/Scripts/Detection/BoundingBoxMapper.cs`
- `Assets/Scripts/Detection/CocoClassNames.cs`
- `Assets/Plugins/iOS/YoloCoreMLPlugin.mm`
- `Assets/Scripts/FoundationPose/FoundationPoseCenterYoloAdapter.cs`
- `Assets/Scripts/FoundationPose/YoloBBoxToMaskAdapter.cs`
- `Assets/FoundationPoseTest.unity`
- `Assets/StreamingAssets/yolo11n.mlpackage`
- `Models/yolo11n.pt`

FoundationPose adaptation behavior:

- `FoundationPoseCenterYoloAdapter` runs early with `[DefaultExecutionOrder(-100)]`.
- It captures AR camera CPU image at `targetFps`.
- It converts the camera image to RGBA32 at `640 x 480` by default.
- It calls old-style `YoloCoreMLDetector.TryDetectCenterObject(...)`.
- It maps the returned screen-space rect back to YOLO input image coordinates with `BoundingBoxMapper.ScreenRectToImageRoi(...)`.
- It converts that ROI to `FPBBoxCoordinateSpace.NormalizedTopLeft`.
- It calls `YoloBBoxToMaskAdapter.SetBoundingBox(...)` with the AR camera image timestamp.
- `FoundationPoseFrameStreamer` later calls `YoloBBoxToMaskAdapter.TryBuildMask(...)` during registration frame creation.
- The mask is a rectangular bbox mask, not a semantic segmentation mask.

Scene expectations:

- `FoundationPose Mask` object should include:
  - `YoloBBoxToMaskAdapter`
  - disabled `FoundationPoseTestBBox`
  - enabled `FoundationPoseCenterYoloAdapter`
- `YoloBBoxToMaskAdapter.maxMaskAgeMs` should not be huge. Current working value is around `300 ms` because YOLO and frame streaming are asynchronous.
- `FoundationPoseFrameStreamer.maskSource` should point to the `YoloBBoxToMaskAdapter`.

Expected logs:

- `[M1 YOLO] CoreML YOLO loaded with MLComputeUnitsAll: ...`
- `[FoundationPoseCenterYoloAdapter] center_object class=... conf=...`
- `[FoundationPoseFrameStreamer] Using registration mask bbox=...`

## YOLO11 Compatibility Notes

The old plugin was written for YOLOv8-style raw `84 x 8400` output.

Current model requirement:

- Use `yolo11n`.
- Keep iPhone runtime on CoreML.
- Host should not run YOLO.

Important compatibility issue:

- Ultralytics/CoreML export for `yolo11n.mlpackage` may expose pipeline outputs named `coordinates` and `confidence` rather than a single raw `84 x 8400` array.
- The adapted plugin must support both:
  - old single-array raw output
  - YOLO11 split output: `coordinates` + `confidence`
- For split output:
  - `coordinates` count must be divisible by 4.
  - `confidence` count must be divisible by 80.
  - prediction count is min of both derived counts.
  - select best class per prediction from confidence vector.
  - preserve the old center-object selection rule.

## Xcode Build Notes

Known failure:

- Xcode may show `Build input file cannot be found` for stale plugin references.
- A stale `FPYoloVisionPlugin.mm` reference came from an abandoned earlier implementation and must not remain in `Unity-iPhone.xcodeproj/project.pbxproj`.

Required runtime plugin:

- `YoloCoreMLPlugin.mm`

Required build output locations:

- Unity asset:
  - `Assets/Plugins/iOS/YoloCoreMLPlugin.mm`
- Xcode generated project:
  - `builds/Libraries/Plugins/iOS/YoloCoreMLPlugin.mm`
- Model in generated Xcode project:
  - `builds/Data/Raw/yolo11n.mlpackage`

Validation commands used:

```bash
xcrun --sdk iphoneos clang -fobjc-arc -fmodules -fsyntax-only Assets/Plugins/iOS/YoloCoreMLPlugin.mm
xcrun --sdk iphoneos clang -fobjc-arc -fmodules -fsyntax-only builds/Libraries/Plugins/iOS/YoloCoreMLPlugin.mm
xcrun coremlcompiler compile Assets/StreamingAssets/yolo11n.mlpackage /private/tmp/yolo11n_compile
```

Mac CoreML `predict()` is not reliable in this environment because it has hit Apple MPSGraph/MLIR crashes. Prefer `coremlcompiler compile` for static validation and real iPhone testing for runtime validation.

## Do Not Forget

- The old folder's Python YOLO is the reference algorithm path, not the iPhone runtime.
- The iPhone runtime is CoreML through `YoloCoreMLPlugin.mm`.
- Keep class filtering disabled unless explicitly requested.
- The target object is selected by the screen/image center rule, not by class name.
- If a detection looks like the wrong object, first inspect coordinate mapping, model output parsing, orientation, and screen/image scale conversion before changing thresholds or class filters.
- Host-side FoundationPose should remain unchanged unless there is a proven FPFRAME/protocol bug.
