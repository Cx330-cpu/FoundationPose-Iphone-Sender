# FoundationPose -> Unity/iPhone 工程交接文档

面向对象：接下来在 Mac Unity 项目 `/Users/tongbingwen/FoundationPose-Iphone-Sender/` 中继续开发的 Codex Agent。

当前 PC 仓库：`/home/brandon/projects/FoundationPose-main`  
当前下游 Unity 项目预期路径：`/Users/tongbingwen/FoundationPose-Iphone-Sender/`

注意：本交接文档基于当前 FoundationPose 仓库真实代码审查、当前仓库内 `unity_sender/` C# 代码审查、以及此前已经完成的验证记录整理。当前 WSL 环境中无法访问 `/Users/tongbingwen/FoundationPose-Iphone-Sender/`，所以下游 Unity 项目里的 scene/Inspector 引用和临时脚本必须由 Unity Agent 重新确认。

## 1. Overall Architecture

当前已经跑通的目标链路：

```text
iPhone Unity
  |
  | AR Foundation
  |
  |-- RGB camera
  |-- raw LiDAR environment depth
  |-- camera intrinsics K
  |-- first-frame registration mask
  |
  v
FPFRAME v1
  |
TCP
  |
Windows / Docker / RTX GPU
  |
NetworkFrameProvider
  |
FoundationPose
  |
REGISTER
  |
TRACK
  |
4x4 pose matrix: ob_in_cam
```

Mac 只负责 Unity/Xcode 开发；最终运行 demo 不需要 Mac。最终目标是 iPhone + Windows PC。PC 端监听 TCP，Unity/iPhone 端只发送 FPFRAME，目前没有 PC -> Unity pose 返回。

## 2. Repository Audit 摘要

已审查的 PC 文件：

| 路径 | 职责 | Unity 是否需要知道 | 是否建议 Unity Agent 修改 |
|---|---|---:|---|
| `run_live.py` | live/recorded 推理入口，包含 `RecordedFoundationPoseProvider` 和 `NetworkFrameProvider`，实现 REGISTER/TRACK 状态机和 pose 输出 | 是 | 不建议，除非协议 bug 已被 inspection 证明 |
| `run_demo.py` | 官方/本仓库回归 baseline，读取 `demo_data/mustard0` | 只需知道 baseline | 冻结，除明确 bug 外不要改 |
| `network_frame_protocol.py` | FPFRAME v1 Python 编解码与校验 | 是 | 冻结协议，先用 inspection 证明问题 |
| `estimater.py` | FoundationPose 核心 REGISTER/TRACK；返回 `ob_in_cam` | 只需理解 pose 语义 | 不建议修改 |
| `datareader.py` | demo/recorded 数据读取；depth PNG mm -> meters | 只需理解 recorded baseline | 不建议修改 |
| `Utils.py` | 渲染、投影、可视化、`mycpp` 导入等共享工具 | 否 | 不建议修改 |
| `inspect_fpframe_stream.py` | TCP 接收 FPFRAME 并保存 RGB/depth/mask/K，支持 synthetic exactness 检查 | 是 | 可作为调试工具扩展 |
| `send_recorded_frames.py` | 把 recorded demo 通过 FPFRAME 发给 PC receiver | 可选 | 一般不改 |
| `check_network_packets.py` | 本地自测 FPFRAME round-trip exactness | 可选 | 一般不改 |
| `docker/` | 原 3070 Docker 与新增 5070 Ti Docker | 可选 | Unity Agent 通常不改 |
| `unity_sender/Assets/Scripts/FoundationPose/` | Unity sender 参考实现 | 是 | 下游 Unity 侧主要参考/迁移对象 |

当前重要数据目录：

| 路径 | 内容 |
|---|---|
| `demo_data/mustard0/mesh/textured_simple.obj` | 默认 mesh |
| `demo_data/mustard0/cam_K.txt` | demo K，3x3 |
| `demo_data/mustard0/rgb/` | 737 张 RGB PNG，首帧 `640x480` RGBA 文件，读取时取 `[..., :3]` |
| `demo_data/mustard0/depth/` | 16-bit grayscale PNG，单位 mm，PC 读取后 `/1000` 成 meters |
| `demo_data/mustard0/masks/` | 当前只有首帧 mask |
| `weights/2024-01-11-20-02-45/model_best.pth` | scorer 权重 |
| `weights/2023-10-28-18-33-37/model_best.pth` | refiner 权重 |

当前 debug/output 目录结构包括 `debug/`, `debug_live/`, `debug_live_iphone/`, `debug_fpframe_inspect/`, `debug_iphone_real/`, `debug_5070ti_docker/` 等。`run_live.py` 输出 pose 到 `<debug_dir>/ob_in_cam/<frame_id>.txt`，如果 `--save_track_vis` 或 `--debug >= 1` 会生成 overlay，`--save_track_vis` 写入 `<debug_dir>/track_vis/`。

## 3. run_live.py 完整接口

真实 CLI 参数来自 `run_live.py`：

| 参数 | 类型/choices | 默认值 | 说明 |
|---|---|---|---|
| `--mesh_file` | str | `<repo>/demo_data/mustard0/mesh/textured_simple.obj` | PC 端加载的目标物体 mesh |
| `--test_scene_dir` | str | `<repo>/demo_data/mustard0` | recorded provider 的数据目录 |
| `--est_refine_iter` | int | `5` | REGISTER refinement iteration |
| `--track_refine_iter` | int | `2` | TRACK refinement iteration |
| `--debug` | int | `1` | `>=1` 时 `cv2.imshow('FoundationPose Live', ...)` |
| `--debug_dir` | str | `<repo>/debug_live` | pose/visualization 输出目录 |
| `--save_track_vis` | flag | false | 保存 overlay PNG 到 `track_vis/` |
| `--provider` | `recorded`/`network` | `recorded` | frame source |
| `--host` | str | `0.0.0.0` | network provider bind host |
| `--port` | int | `5000` | network provider listen port |

推荐 PC live 命令：

```bash
python run_live.py \
  --provider network \
  --host 0.0.0.0 \
  --port 5000 \
  --debug 1 \
  --debug_dir debug_live_iphone
```

如果没有 GUI/X11，使用：

```bash
python run_live.py --provider network --host 0.0.0.0 --port 5000 --debug 0 --debug_dir debug_live_iphone
```

`recorded` provider：从 `YcbineoatReader` 读取 `demo_data/mustard0`。第 0 帧附带 `reader.get_mask(0)` 作为 REGISTER mask，后续帧 mask 为 `None`。  
`network` provider：TCP accept 一个 sender，调用 `network_frame_protocol.receive_frame()` 得到 `rgb/depth/K/mask/header`。

状态机：

```text
UNREGISTERED
  first frame must contain mask
  -> est.register(...)
  -> state = TRACKING

TRACKING
  later frames do not need mask
  -> est.track_one(...)
```

第一帧必须有 mask，因为 REGISTER 需要根据 mask 估计初始目标区域和深度中心。后续 TRACK 使用上一帧 `self.pose_last`，不需要 mask。

## 4. FoundationPose 模型 / Mesh 参数

FoundationPose 当前不是“任意 RGB/depth 自动知道是什么物体”。PC 端必须提前加载目标物体 mesh。

默认 mesh：

```text
/home/brandon/projects/FoundationPose-main/demo_data/mustard0/mesh/textured_simple.obj
```

`run_demo.py` 和 `run_live.py` 默认都使用该 mesh。mesh 在 REGISTER/TRACK 中用于：

- 生成 mesh tensor；
- nvdiffrast 渲染；
- scorer/refiner 对齐；
- 可视化 3D bbox/axis。

FPFRAME v1 当前不传 OBJ、不传 mesh。`mesh currently stays on PC side.` Unity 端只传 RGB/depth/K/mask。Unity 检测出的 object 必须和 PC 当前加载的 mesh 对应；如果切换物体，PC 端至少要替换 `--mesh_file`，并确认该物体对应权重/场景假设可用。Unity Agent 不要尝试通过 FPFRAME 传 OBJ。

## 5. FPFRAME v1 协议

TCP message layout：

```text
4-byte big-endian uint32 header_len
+ UTF-8 JSON header
+ RGB blob
+ depth blob
+ optional mask blob
```

Python 常量：

```python
MAGIC = "FPFRAME"
VERSION = 1
MAX_HEADER_LEN = 1024 * 1024
```

Header 字段和约束：

| 字段 | 类型 | 约束/语义 |
|---|---|---|
| `magic` | string | 必须 `"FPFRAME"` |
| `version` | int | 必须 `1` |
| `frame_id` | string | frame identifier；Unity 用 timestamp ns 字符串 |
| `index` | int | `>=0` |
| `timestamp` | number/null in Python; double in Unity | 秒；如果非 null 必须 finite |
| `width` | int | `>0` |
| `height` | int | `>0` |
| `fx` | float | finite 且 `>0` |
| `fy` | float | finite 且 `>0` |
| `cx` | float | finite |
| `cy` | float | finite |
| `rgb_format` | string | `"png"` 或 `"jpeg"` |
| `depth_format` | string | 必须 `"uint16_png_mm"` |
| `mask_format` | string | `"none"` 或 `"uint8_png"` |
| `rgb_len` | int | `>0` |
| `depth_len` | int | `>0` |
| `mask_len` | int | `>=0`; 为 0 时 `mask_format == "none"` |

PC decode：

- RGB：`cv2.imdecode(..., IMREAD_COLOR)` 得 BGR，再 `cv2.cvtColor(..., COLOR_BGR2RGB)`，最终 `rgb` 是 RGB。
- Depth：`cv2.imdecode(..., IMREAD_UNCHANGED)`，要求 `uint16` 2D，再 `/1000.0` 得 meters。
- Mask：如果 `mask_len > 0`，decode 2D，`mask_img > 0` 得 bool。
- K：

```text
fx  0 cx
0  fy cy
0   0  1
```

RGB/depth/mask shape 必须完全等于 `(height, width)` coordinate system。

## 6. RGB 数据约定

Unity sender semantic RGB：

```text
shape = H x W x 3
dtype = uint8
channel order = RGB
```

当前 PNG 是 correctness/default test codec。PC OpenCV decode 时先得到 BGR，但协议层会转换回 RGB。Unity Agent 不要再额外做 BGR swap。

已验证 synthetic exactness：

```text
RGB identity exact = True
max_diff = 0
```

语义结论：没有额外 `flipud`、没有 `fliplr`、没有 BGR swap。注意：`FPFrameProtocol.EncodeRgb()` 内部为了 Unity PNG encoder 的内存行约定调用 `FlipRows()`；这是实现细节，已被 synthetic identity 验证覆盖。不要在业务层再加 rotate/flip。

## 7. Depth 数据约定

Unity 端：

```text
meters -> round(depth * 1000) -> uint16 millimeters -> PNG
```

当前 `FoundationPoseFrameStreamer` 支持：

- `XRCpuImage.Format.DepthFloat32`：float meters -> `MetersToMillimeters()`
- `XRCpuImage.Format.DepthUint16`：直接按 uint16 mm 读取

PC 端：

```text
uint16 PNG -> /1000.0 -> meters
```

协议字段：

```text
depth_format = uint16_png_mm
```

RGB、Depth、Mask、K 必须处于同一 final pixel coordinate system。当前 Unity frame streamer 选择 raw depth resolution 为 final resolution。

## 8. Camera Intrinsics K

Unity 当前代码：`ARCameraManager.TryGetIntrinsics(out XRCameraIntrinsics intrinsics)`，然后按 final depth resolution 缩放：

```csharp
scaleX = finalWidth / (double)intrinsics.resolution.x;
scaleY = finalHeight / (double)intrinsics.resolution.y;
fx' = intrinsics.focalLength.x * scaleX;
fy' = intrinsics.focalLength.y * scaleY;
cx' = intrinsics.principalPoint.x * scaleX;
cy' = intrinsics.principalPoint.y * scaleY;
```

最终：

```text
RGB / Depth / Mask / K
```

必须完全处于同一 pixel coordinate system。Unity Agent 接 YOLO 时不能只换 bbox 坐标，不同步考虑 final image resolution。

## 9. 当前 iPhone 真实数据状态

已验证真实测试状态（历史验证记录）：

- iPhone 成功 TCP 连接 Windows；
- real RGB 已发送；
- raw LiDAR environment depth 已发送；
- K 已发送；
- first-frame mask 已发送；
- REGISTER 成功；
- 后续 TRACK 成功；
- pose matrix 持续输出。

真实测试实例，不是硬编码常数：

```text
resolution: 256 x 192
fx/fy ≈ 188
cx ≈ 128.6
cy ≈ 96.46
depth example range ≈ 126 mm ~ 634 mm
```

已肉眼确认：RGB 与 depth 空间位置基本一致。

## 10. Orientation / iPhone Portrait 问题

重要：当前 iPhone 竖屏拿着时，PC 保存的 raw RGB 看起来是 landscape / 旋转 90 度。但当前：

```text
RGB
Depth
K
```

处于同一个 raw pixel coordinate system，并且 FoundationPose REGISTER/TRACK 已能运行。因此当前不要为了视觉效果单独 rotate RGB。

绝对不要单独 rotate RGB。

如果未来要把整个 pipeline 正规化成 portrait，必须同步变换：

```text
RGB
Depth
Mask / bbox
K
```

并正确更新：

```text
width / height
fx / fy
cx / cy
```

当前建议：先保持 raw camera coordinate system，UI/orientation normalization 后做。

## 11. Mask / YOLO 接口

当前仓库中的 `unity_sender/Assets/Scripts/FoundationPose/YoloBBoxToMaskAdapter.cs` 不是 detector。它的职责是：

```text
bbox -> rectangle binary mask -> first registration frame
```

核心接口：

```csharp
public void SetBoundingBox(Rect newBBox, double timestamp, FPBBoxCoordinateSpace space)
```

真实 enum：

```csharp
public enum FPBBoxCoordinateSpace
{
    FinalImagePixelsTopLeft,
    NormalizedTopLeft,
    NormalizedBottomLeft
}
```

转换语义：

- `FinalImagePixelsTopLeft`：`Rect` 已经是 final image pixel 坐标，origin top-left。
- `NormalizedTopLeft`：`x/y/w/h` 为 0..1，origin top-left。
- `NormalizedBottomLeft`：`x/y/w/h` 为 0..1，origin bottom-left；代码使用 `(1 - yMax) * height` 到 `(1 - yMin) * height` 转 top-left。

`maxMaskAgeMs` 真实默认值为 `50.0` ms。`bboxInflation` 默认 `1.0`。

Vision bbox 很可能是 normalized bottom-left，但 Unity Agent 必须重新确认真实 YOLO/CoreML/Vision 输出。FPFRAME 最终 pixel coordinate system 当前按发送的 final image 解释，bbox 必须显式指定 coordinate space。

## 12. TEMPORARY: 当前 TestBBox 状态

历史测试中，为验证 iPhone real pipeline，下游 Unity 项目临时加入了 `FoundationPoseTestBBox`，持续给 `YoloBBoxToMaskAdapter` 一个固定中央 rectangle bbox。

`FoundationPoseTestBBox` 当前不在本仓库 `unity_sender/Assets/Scripts/FoundationPose/` 中；Unity Agent 必须在 `/Users/tongbingwen/FoundationPose-Iphone-Sender/` 中重新查找确认。

这是 smoke test ONLY，不是最终实现。

历史测试还临时把 `YoloBBoxToMaskAdapter.maxMaskAgeMs` 设为非常大的数，以绕过测试 bbox timestamp 与 ARKit frame timestamp 不同 clock domain 的问题。原设计默认值已从代码确认：`50.0` ms。

接真实 YOLO 时：

1. 删除或 disable `FoundationPoseTestBBox`。
2. `maxMaskAgeMs` 恢复正常量级，约 `50 ms`。
3. YOLO bbox timestamp 必须和 `FoundationPoseFrameStreamer` 用于 freshness 判断的 timestamp 使用同一时钟基准。
4. 不允许永久关闭 timestamp freshness check。

## 13. Timestamp / Clock Domain

已遇到的问题：

```text
registration_mask_unavailable
stale_yolo_bbox
age_ms ≈ 8,668,xxx ms
```

原因不是 bbox 真过期，而是 clock-domain mismatch：

- 测试 bbox 使用 `Time.realtimeSinceStartupAsDouble`；
- `FoundationPoseFrameStreamer` freshness 比较使用 `XRCpuImage.timestamp`：`rgbImage.timestamp`。

当前代码中最合理的统一 timestamp 来源：让真实 YOLO pipeline 以同一帧的 `rgbImage.timestamp` 或可映射到 `rgbImage.timestamp` 的 frame timestamp 作为 `SetBoundingBox(... timestamp ...)`。如果 YOLO 是异步 Vision/CoreML，bridge 必须携带源 camera frame timestamp，而不是用处理完成时的 `Time.realtimeSinceStartupAsDouble`。

这里只做设计说明，不要现在擅自重构 PC core。

## 14. 当前 FoundationPose Smoke Test 结果

历史真实测试已验证：

```text
state = TRACKING
operation = TRACK
连续超过 100 帧
```

真实示例 processing_time：

```text
约 0.028 ~ 0.050 sec
```

tracking processing_fps：

```text
约 20 ~ 35 FPS
```

该性能数据来自 RTX 3070 测试实例，不是保证值。当前 5070 Ti Docker 环境也已通过 `run_demo.py` `DEBUG=0` 完整跑通。

Pose 输出格式：

```text
4 x 4 homogeneous transform matrix

R00 R01 R02 tx
R10 R11 R12 ty
R20 R21 R22 tz
0   0   0   1
```

语义从代码变量名和保存路径确认：输出为 `ob_in_cam`，即 object-in-camera transform。`run_live.py` 保存：

```python
np.savetxt(f'{debug_dir}/ob_in_cam/{packet.frame_id}.txt', pose.reshape(4,4))
```

`estimater.py` 中 REGISTER 返回：

```python
best_pose = poses[0] @ self.get_tf_to_centered_mesh()
```

TRACK 返回：

```python
return (pose @ self.get_tf_to_centered_mesh()).data.cpu().numpy().reshape(4,4)
```

其中 `self.pose_last` 是 centered mesh pose，返回值是原始 object mesh pose in camera。可视化时 `run_live.py` 用：

```python
center_pose = pose @ np.linalg.inv(to_origin)
```

用于 oriented bbox/axis 绘制。Unity pose 回传阶段必须严肃处理 FoundationPose camera frame 与 Unity/ARKit world/camera 坐标系转换。

## 15. PC -> Unity Pose Return 尚未实现

当前通信只有：

```text
Unity/iPhone -> PC
```

尚未实现：

- PC -> Unity pose return；
- ACK；
- pose response protocol；
- tracking-loss notification；
- re-register request；
- Unity coordinate conversion；
- Unity 端接收 4x4 pose；
- object pose 放进 Unity world。

Unity Agent 不要假设 TCP sender 已经能接收 pose。

## 16. Docker 环境

当前镜像：

```text
foundationpose:latest   原始 Docker，CUDA 11.3 / PyTorch cu118 路径，面向 3070/旧环境
foundationpose:5070ti   新 Docker，CUDA 12.8.1 / PyTorch nightly cu128 / sm_120
```

5070 Ti 已验证：

- `nvidia-smi` 识别 `NVIDIA GeForce RTX 5070 Ti`；
- `torch 2.12.0.dev20260408+cu128`；
- `torch.cuda.get_arch_list()` 包含 `sm_120`；
- `pytorch3d`、`nvdiffrast.torch`、`open3d` 导入成功；
- Docker 专用 `mycpp/build_5070ti_docker/mycpp.cpython-310-x86_64-linux-gnu.so` 已编译；
- 但 Docker 下 native `mycpp.cluster_poses()` 曾 segfault，当前 `docker/run_demo_5070ti.sh` 默认 `FOUNDATIONPOSE_USE_PY_CLUSTER=1` 使用 Python fallback。

5070 Ti 运行：

```bash
cd docker
bash run_container_5070ti.sh
bash docker/build_extensions_5070ti.sh
DEBUG=0 bash docker/run_demo_5070ti.sh
```

如果需要 GUI 窗口，`DEBUG=1` 依赖 X11/WSL/Docker display 配置；没有窗口不等于模型没跑。`DEBUG=2` 会保存 `debug_5070ti_docker/track_vis/`。

## 17. Validation History

已完成验证记录：

1. `run_live` recorded vs `run_demo`：

```text
737/737
allclose_all_frames = True
max_abs_diff = 0
```

2. network recorded PNG vs recorded：

```text
737/737
allclose_all_frames = True
max_abs_diff = 0
```

3. Unity Synthetic Phase A：

```text
RGB identity exact = True
Depth identity exact = True
K exact = True
Mask identity exact = True
```

4. Mac Unity synthetic：

```text
FPFRAME -> run_live -> REGISTER/TRACK -> Pose UI
成功
```

5. iPhone real：

```text
real RGB -> real raw depth -> K -> temporary bbox mask -> FPFRAME
-> run_live -> REGISTER -> TRACK -> continuous pose
成功
```

6. 5070 Ti Docker：

```text
foundationpose:5070ti image exists
CUDA/PyTorch/PyTorch3D/nvdiffrast/open3d import OK
DEBUG=0 bash docker/run_demo_5070ti.sh exits 0
```

## 18. Command Cheat Sheet

PC input inspection：

```bash
python inspect_fpframe_stream.py \
  --host 0.0.0.0 \
  --port 5000 \
  --num_frames 10 \
  --save_dir debug_iphone_real
```

Synthetic exactness inspection：

```bash
python inspect_fpframe_stream.py \
  --host 0.0.0.0 \
  --port 5000 \
  --num_frames 3 \
  --save_dir debug_fpframe_inspect \
  --expect_synthetic
```

FoundationPose live network：

```bash
python run_live.py \
  --provider network \
  --host 0.0.0.0 \
  --port 5000 \
  --debug 1 \
  --debug_dir debug_live_iphone
```

Headless live：

```bash
python run_live.py \
  --provider network \
  --host 0.0.0.0 \
  --port 5000 \
  --debug 0 \
  --debug_dir debug_live_iphone
```

Recorded sender local test：

```bash
python send_recorded_frames.py \
  --host 127.0.0.1 \
  --port 5000 \
  --test_scene_dir demo_data/mustard0 \
  --rgb_codec png
```

Packet regression:

```bash
python check_network_packets.py \
  --host 0.0.0.0 \
  --connect_host 127.0.0.1 \
  --port 5001
```

PC listen endpoint:

```text
0.0.0.0:5000
```

当前 Windows LAN IP 历史实例：

```text
192.168.31.183
```

这是当前开发环境 IP，不应硬编码为永久架构常量。

## 19. Unity 项目当前预计关键组件

Unity Agent 应在 `/Users/tongbingwen/FoundationPose-Iphone-Sender/` 中重新检查：

- `FoundationPoseTcpSender`
- `FoundationPoseFrameStreamer`
- `YoloBBoxToMaskAdapter`
- `FoundationPoseSyntheticSender`
- `FPFrameProtocol`
- `FoundationPoseTestBBox`（TEMPORARY，当前 PC 仓库不可见）
- `ARSession`
- `XROrigin / Main Camera`
- `ARCameraManager`
- `AROcclusionManager`

不要假设 Inspector 引用仍然正确；必须在 Unity scene 中确认。

## 20. Unity Agent 下一阶段任务

按优先级：

1. 迁移/接入真实 CoreML/Vision YOLO detection。尽量不要修改已经验证的 FoundationPose core sender files。目标数据流：

```text
ARCamera RGB
-> CoreML/Vision YOLO
-> bbox
-> small bridge
-> YoloBBoxToMaskAdapter.SetBoundingBox(...)
-> registration mask
-> FoundationPoseFrameStreamer
```

2. 解决 YOLO bbox coordinate system。确认 YOLO 输出到底是 `NormalizedBottomLeft`、`NormalizedTopLeft` 还是 pixel coordinate，再传正确 `FPBBoxCoordinateSpace`。

3. 解决 timestamp clock domain。恢复 `maxMaskAgeMs ≈ 50 ms`，并保证 bbox timestamp 与 camera frame timestamp 可比较。

4. PC 用 `inspect_fpframe_stream.py` 查看真实 mask。mask 必须覆盖目标、不翻转、不旋转错位、不缩放错位，并与 RGB/depth 对齐。

5. 再跑 `run_live.py --provider network`，验证真实 YOLO bbox mask 下 REGISTER/TRACK/pose output。

6. bbox smoke test 稳定后，再考虑 YOLO-Seg、SAM 或其他 true segmentation。不要一开始就重构成 segmentation。

## 21. FUTURE: 更远的工作

暂不开发：

1. PC pose -> Unity 返回协议；
2. Unity 接收 4x4 pose；
3. FoundationPose / OpenCV / Unity 坐标系转换；
4. object pose 放进 Unity world；
5. AR object replacement；
6. tracking loss detection；
7. re-registration；
8. ACK；
9. network reconnect/recovery；
10. portrait orientation normalization；
11. 公网网络/远程 server；
12. latency optimization；
13. JPEG / compression / bandwidth tuning。

## 22. Do Not Break / Frozen Baseline

当前稳定 baseline：

- `run_demo.py`：regression baseline，除明确 bug 外不要修改；
- `estimater.py` / `datareader.py` / `Utils.py`：FoundationPose core，Unity Agent 不应随意修改；
- `network_frame_protocol.py`：FPFRAME v1 协议，先 inspection 再改；
- `run_live.py` network workflow：REGISTER/TRACK 状态机已验证；
- FPFRAME v1 数据约定；
- 当前 REGISTER/TRACK state machine；
- `demo_data/mustard0` + weights baseline。

如果 Unity Agent 怀疑协议问题：先用 `inspect_fpframe_stream.py` 保存真实 RGB/depth/mask/K 并证明问题，再考虑协议修改。

## 23. 文档描述与代码不一致 / 风险点

审查发现的关键不一致或风险：

- 用户历史中提到的 `FoundationPoseTestBBox` 不在当前 PC 仓库 `unity_sender/` 中；它应在下游 Unity 项目中确认。
- 当前 `FPFrameProtocol.EncodeRgb/EncodeDepthPng/EncodeMaskPng` 内部会 `FlipRows()`，但 synthetic exactness 已证明最终 PC 端 identity；Unity Agent 不应因看到 flip 实现而在上层再翻转。
- `python run_demo.py` 直接运行不会自动设置 Docker 脚本中的 env；当前 `estimater.py` 已加 fallback，但 Unity Agent 仍应用推荐脚本。
- 5070 Ti Docker 下 native `mycpp.cluster_poses()` 曾 segfault，当前通过 `FOUNDATIONPOSE_USE_PY_CLUSTER=1` fallback 绕开。该 fallback 只影响初始化 pose grid 聚类，不改模型权重或网络推理。
- 当前没有 PC -> Unity pose response，Unity 端 sender 是单向发送。

## 24. Unity Agent 最优先解决的 3 个问题

1. 用真实 CoreML/Vision YOLO 替换 TEMPORARY fixed bbox，并只写最小 bridge 到 `YoloBBoxToMaskAdapter.SetBoundingBox(...)`。
2. 彻底确认 bbox coordinate space，并用 `inspect_fpframe_stream.py` 证明 mask 与 RGB/depth/K 对齐。
3. 统一 timestamp clock domain，恢复 `maxMaskAgeMs` 正常值，不要永久关闭 freshness check。

## Unity Agent — Start Here

1. 完整阅读本文档。
2. 第一件事不要修改 FoundationPose PC core，不要修改 FPFRAME 协议，不要开始做 pose return。
3. 在 `/Users/tongbingwen/FoundationPose-Iphone-Sender/` 审查 Unity scene 和当前 FoundationPose scripts，确认 Inspector 中 `ARCameraManager`、`AROcclusionManager`、`FoundationPoseTcpSender`、`FoundationPoseFrameStreamer`、`YoloBBoxToMaskAdapter` 引用有效。
4. 确认 TEMPORARY `FoundationPoseTestBBox` pipeline 仍能 build/run，并明确它只是 smoke test。
5. 找到或迁移现有 CoreML/Vision YOLO implementation。
6. 写最小 bbox bridge：YOLO bbox -> `YoloBBoxToMaskAdapter.SetBoundingBox(Rect bbox, double timestamp, FPBBoxCoordinateSpace space)`。
7. 明确 YOLO bbox coordinate system 和 timestamp 来源；不要猜。
8. 在 PC 端先运行 `inspect_fpframe_stream.py`，保存真实 mask/RGB/depth/K，确认 mask 覆盖目标且没有翻转/旋转/缩放错位。
9. 再运行 `run_live.py --provider network`，成功判定是 first frame REGISTER 成功，后续进入 `state = TRACKING`、`operation = TRACK`，持续输出 `ob_in_cam` pose。
10. 只有 bbox smoke test 稳定后，才进入 segmentation、pose return、Unity world coordinate conversion 等下一阶段。
