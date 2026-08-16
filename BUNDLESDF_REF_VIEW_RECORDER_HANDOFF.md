# BundleSDF Reference View Recorder 交接文档

面向对象：在 Mac Unity 项目中继续开发的 Codex Agent。  
Unity 项目预期路径：`/Users/tongbingwen/FoundationPose-Iphone-Sender/`  
PC/FoundationPose 仓库：`/home/brandon/projects/FoundationPose-main`

## 1. 当前结论

为了让 FoundationPose 追踪用户手里的真实杯子，PC 侧最终需要一个杯子的 OBJ mesh，例如：

```text
glass_cup_ref_views/ob_0000001/model/model.obj
```

FoundationPose 的 model-free 路线不是重新训练 scorer/refiner，而是：

```text
Unity/iPhone 采集多视角 RGB + LiDAR depth + mask + camera pose
  -> 保存为 BundleSDF ref_view_dir
  -> PC 运行 bundlesdf/run_nerf.py 重建 mesh
  -> 导出 model.obj
  -> run_live.py --mesh_file model.obj
```

这个 recorder 应该主要在 Unity/iPhone 项目里实现，因为 Unity/AR Foundation 才能直接拿到每帧 AR camera pose。当前 FPFRAME live 协议不传 camera pose，因此 PC 侧单独接收 live FPFRAME 不能完整生成 BundleSDF 训练目录。

## 2. 不要修改的东西

本任务是新增离线采集工具，不要改 live tracking 行为。

不要修改：

- FPFRAME v1 wire format
- `FoundationPoseFrameStreamer` 当前 live 发送语义
- REGISTER/TRACK 状态机
- PC `network_frame_protocol.py`
- PC `run_live.py` 推理逻辑
- FoundationPose `estimater.py`

可以新增：

- Unity 侧 `BundleSDFRefViewRecorder.cs`
- Unity 侧导出/分享数据目录的工具
- Unity 侧用于调试的 geometry log
- PC 侧只读校验脚本，若需要

## 3. Recorder 的职责

新增 Unity 工具：采集杯子的多视角参考数据，并导出 BundleSDF 能读取的目录结构。

每次采集一个物体，生成一个 object directory：

```text
glass_cup_ref_views/
  ob_0000001/
    K.txt
    select_frames.yml
    rgb/
      000000.png
      000001.png
      ...
    depth_enhanced/
      000000.png
      000001.png
      ...
    mask/
      000000.png
      000001.png
      ...
    cam_in_ob/
      000000.txt
      000001.txt
      ...
```

文件名必须 6 位递增，从 `000000` 开始。四个 per-frame 目录中的同名文件必须来自同一帧或同一时间同步采样。

## 4. 文件格式

### `rgb/000000.png`

- PNG
- 8-bit RGB
- 尺寸必须与 depth/mask 一致，例如 `256x192`
- 坐标系必须与 depth/mask/K 一致
- 不要额外旋转、镜像、裁剪，除非 RGB/depth/mask/K/camera pose 全部同步转换

### `depth_enhanced/000000.png`

- PNG
- 16-bit single channel
- 单位：毫米
- 无效深度写 `0`
- 尺寸必须与 RGB/mask 一致

BundleSDF 读取逻辑来自 PC 仓库：

```python
depth = cv2.imread(color_file.replace('rgb','depth_enhanced'), -1) / 1e3
```

所以 Unity 侧必须导出 `uint16 mm`，不要导出 meters float PNG。

### `mask/000000.png`

- PNG
- 8-bit single channel
- object pixels = `255`
- background = `0`
- 尺寸必须与 RGB/depth 一致
- 坐标系必须覆盖 RGB 里的真实杯子位置

第一版可以使用检测 bbox rasterized mask，但 BundleSDF 重建质量会明显受背景影响。更好的版本应该使用分割 mask。

### `K.txt`

3x3 text matrix，空格分隔，单位为当前导出图像像素坐标：

```text
fx 0 cx
0 fy cy
0 0 1
```

如果 Unity 从 ARCamera intrinsics 得到的是原始 RGB 分辨率的 K，而 recorder 最终保存为 depth 分辨率或缩放后分辨率，必须同步缩放：

```text
scaleX = outputWidth / intrinsicsResolutionWidth
scaleY = outputHeight / intrinsicsResolutionHeight
fx = rawFx * scaleX
fy = rawFy * scaleY
cx = rawCx * scaleX
cy = rawCy * scaleY
```

### `cam_in_ob/000000.txt`

4x4 text matrix，空格分隔。语义必须是：

```text
camera_from_object
```

也就是 object coordinate 中一点 `p_ob` 变换到 camera coordinate：

```text
p_cam = cam_in_ob @ p_ob
```

PC 侧 `bundlesdf/run_nerf.py` 会这样读取：

```python
cam_in_ob = np.loadtxt(...).reshape(4, 4)
```

### `select_frames.yml`

当前 PC 代码只打开这个文件，但没有使用具体内容。为了兼容，写入一个简单合法 YAML：

```yaml
frames:
  - 0
  - 1
  - 2
```

## 5. 坐标系约定

BundleSDF / FoundationPose PC 侧使用 OpenCV camera convention：

```text
x: right
y: down
z: forward
```

Unity world/camera convention 通常不是这个，需要显式转换。不要把 Unity `Camera.transform.worldToLocalMatrix` 直接当作 `cam_in_ob` 写入，除非已经验证它的坐标轴和 OpenCV camera convention 一致。

建议 Unity Agent 实现并打印以下矩阵：

```text
[BUNDLE-REC][POSE]
frame=000000
unity_world_from_camera=...
unity_camera_from_world=...
opencv_camera_from_world=...
opencv_camera_from_object=...
```

如果采集时物体固定不动，可以定义第一帧的 object frame 为：

```text
world_from_object = world_from_camera_at_first_frame
```

或者让用户在 Unity 中放置/确认一个 object anchor：

```text
world_from_object = objectAnchor.localToWorldMatrix
```

然后每帧：

```text
opencv_cam_in_ob = opencv_cam_in_world @ world_in_object
```

关键要求是：所有 frame 的 `cam_in_ob` 必须表达“相机相对同一个静止物体坐标系”的运动。

## 6. 采集策略

目标：围绕杯子采集 16-32 个参考视角。

建议：

- 物体静止，手机绕物体移动
- 保持杯子占画面 25%-70%
- 视角覆盖正面、侧面、上方斜视
- 每帧间隔足够大，不要 32 张几乎一样的图
- 背景尽量简单
- 光照稳定，避免强反光
- 玻璃杯建议放入有纹理的纸、彩色液体，或贴少量临时标记

玻璃杯是困难物体：LiDAR/depth 可能穿透透明表面，导致重建 mesh 失败或变成背景/桌面形状。如果流程验证优先，建议先用不透明杯子跑通。

## 7. Unity 侧推荐实现

新增脚本：

```text
Assets/Scripts/FoundationPose/BundleSDFRefViewRecorder.cs
```

职责：

1. 从 `ARCameraManager.TryAcquireLatestCpuImage` 获取 RGB。
2. 从 `AROcclusionManager.TryAcquireRawEnvironmentDepthCpuImage` 获取 raw environment depth。
3. 选择统一输出尺寸，建议第一版使用 depth 尺寸。
4. 将 RGB 转换到输出尺寸，必须与 depth/mask/K 对齐。
5. 获取或生成当前帧 object mask。
6. 获取当前 AR camera pose。
7. 计算 `cam_in_ob`。
8. 保存 PNG/TXT/YAML 到 Unity app 可写目录。
9. 提供导出/分享目录的方法，方便拷贝到 PC。

建议 Inspector 参数：

```text
objectId = 1
sessionName = glass_cup_ref_views
captureCountTarget = 24
minCaptureIntervalSeconds = 0.4
minCameraTranslationMeters = 0.03
minCameraRotationDegrees = 8
useDepthResolution = true
maskSource = YoloBBoxToMaskAdapter or segmentation provider
```

建议 terminal/Xcode log：

```text
[BUNDLE-REC][CAPTURE]
frame=000000
size=256x192
rgb_ts=...
depth_ts=...
K=(fx,fy,cx,cy)
mask_bbox=(xMin,yMin,xMax,yMax)
depth_valid_count=...
path=...

[BUNDLE-REC][POSE]
frame=000000
cam_in_ob=...
```

## 8. 导出到 PC 后的检查

把目录放到 PC，例如：

```text
/home/brandon/projects/FoundationPose-main/ref_views/glass_cup_ref_views/
  ob_0000001/
```

PC 侧先检查结构：

```bash
find ref_views/glass_cup_ref_views/ob_0000001 -maxdepth 2 -type f | sort | head -80
```

应该看到：

```text
K.txt
select_frames.yml
rgb/000000.png
depth_enhanced/000000.png
mask/000000.png
cam_in_ob/000000.txt
```

## 9. PC 侧训练/重建命令

在 5070 Ti Docker 环境中运行：

```bash
cd /home/brandon/projects/FoundationPose-main
bash docker/run_container_5070ti.sh
```

容器内：

```bash
cd /home/brandon/projects/FoundationPose-main
python bundlesdf/run_nerf.py \
  --ref_view_dir /home/brandon/projects/FoundationPose-main/ref_views/glass_cup_ref_views \
  --dataset ycbv
```

预期输出：

```text
ref_views/glass_cup_ref_views/ob_0000001/model/model.obj
```

然后 live tracking：

```bash
python run_live.py \
  --provider network \
  --host 0.0.0.0 \
  --port 5000 \
  --mesh_file /home/brandon/projects/FoundationPose-main/ref_views/glass_cup_ref_views/ob_0000001/model/model.obj \
  --debug 0 \
  --debug_dir debug_live_glass_cup
```

## 10. 验收标准

Unity Agent 完成后，交付以下内容：

1. Unity 项目里新增 recorder 脚本和使用说明。
2. 一组测试导出的 ref view 目录。
3. 至少 3 帧的文件截图或 listing，证明同名 RGB/depth/mask/cam_in_ob 都存在。
4. Xcode Console 中 `[BUNDLE-REC][CAPTURE]` 与 `[BUNDLE-REC][POSE]` 日志。
5. 明确说明 RGB/depth/mask/K 的输出尺寸和坐标系。
6. 明确说明 `cam_in_ob` 的 Unity -> OpenCV 坐标转换方式。

最重要的检查：在 PC 上打开任意同名 `rgb` 和 `mask`，mask 必须覆盖 RGB 中的杯子；depth 必须和 RGB 同尺寸；`K.txt` 必须对应这个尺寸。

