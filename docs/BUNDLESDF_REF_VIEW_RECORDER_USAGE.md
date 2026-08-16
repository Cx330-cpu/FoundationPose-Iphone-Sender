# BundleSDF Reference View Recorder V1

This recorder captures offline reference views on iPhone for BundleSDF mesh reconstruction.

## Unity Setup

Add or enable `BundleSDFRefViewRecorder` in the FoundationPose test scene.

Recommended references:

- `cameraManager`: the AR Camera `ARCameraManager`
- `occlusionManager`: the AR Camera `AROcclusionManager`
- `maskSource`: the existing `YoloBBoxToMaskAdapter`
- `objectAnchor`: optional. If empty, the first successfully captured camera pose becomes `world_from_object`.

Recommended settings:

- `sessionName`: `glass_cup_ref_views`
- `objectId`: `1`
- `captureCountTarget`: `24`
- `useDepthResolution`: enabled

The recorder is independent from live FPFRAME streaming. It does not change REGISTER/TRACK behavior.

## iPhone Capture

1. Run the app on iPhone.
2. Make sure YOLO/mask is covering the target object.
3. Tap `Start BundleSDF Rec`.
4. Move the phone to a stable viewpoint and hold still.
5. Tap `Take Ref View`.
6. Move to the next viewpoint, hold still, and tap `Take Ref View` again.
7. Repeat until 16-32 views are captured. The button shows the counter.
8. Tap `Stop Rec`, or wait until `captureCountTarget` is reached.
9. Tap `Log Output Path` to print and copy the app sandbox object directory path.

The output is saved under:

```text
Application.persistentDataPath/BundleSDFRefViews/<sessionName>/ob_0000001/
```

Xcode Console logs:

```text
[BUNDLE-REC][CAPTURE]
[BUNDLE-REC][POSE]
```

Use Xcode Devices and Simulators or Finder app container export to copy the printed directory to the PC.

## Output Format

The object directory is:

```text
glass_cup_ref_views/
  ob_0000001/
    K.txt
    select_frames.yml
    rgb/
      000000.png
    depth_enhanced/
      000000.png
    mask/
      000000.png
    cam_in_ob/
      000000.txt
```

Frame files use 6-digit names starting from `000000`.

`rgb`, `depth_enhanced`, `mask`, and `K.txt` use the same output size. V1 defaults to raw environment depth resolution, usually `256x192`.

`depth_enhanced/*.png` is `uint16` millimeters. Invalid depth is `0`.

`mask/*.png` is `uint8`: object `255`, background `0`.

## Pose Convention

`cam_in_ob/*.txt` is a 4x4 matrix:

```text
camera_from_object
p_cam = cam_in_ob @ p_ob
```

The camera coordinate convention is OpenCV:

```text
x right
y down
z forward
```

The recorder first computes the Unity-convention rigid transform:

```text
unity_camera_from_object = unity_camera_from_world * world_from_object^-1
```

Then it converts both camera and object bases:

```text
C = diag(1, -1, 1)
opencv_camera_from_object = C * unity_camera_from_object * C
```

If `objectAnchor` is not assigned, the first captured camera pose defines `world_from_object`.

The exported rotation must satisfy:

```text
det(R) ~= +1
cross(R[:,0], R[:,1]) ~= R[:,2]
```

## PC Check

Copy the exported session to:

```text
/home/brandon/projects/FoundationPose-main/ref_views/glass_cup_ref_views/
```

Then check:

```bash
find ref_views/glass_cup_ref_views/ob_0000001 -maxdepth 2 -type f | sort | head -80
```

Expected files:

```text
K.txt
select_frames.yml
rgb/000000.png
depth_enhanced/000000.png
mask/000000.png
cam_in_ob/000000.txt
```

Important visual check: open any same-index RGB and mask. The mask must cover the cup in RGB, and depth must have the same size.

## BundleSDF

Inside the 5070 Ti Docker container:

```bash
cd /home/brandon/projects/FoundationPose-main
python bundlesdf/run_nerf.py \
  --ref_view_dir /home/brandon/projects/FoundationPose-main/ref_views/glass_cup_ref_views \
  --dataset ycbv
```

Expected mesh:

```text
ref_views/glass_cup_ref_views/ob_0000001/model/model.obj
```
