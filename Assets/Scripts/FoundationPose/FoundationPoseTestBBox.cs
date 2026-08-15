using UnityEngine;
using FoundationPoseStreaming;

public class FoundationPoseTestBBox : MonoBehaviour
{
    public YoloBBoxToMaskAdapter maskAdapter;

    void Update()
    {
        if (maskAdapter == null)
            return;

        // 屏幕中央 50% 区域
        Rect bbox = new Rect(
            0.25f,
            0.25f,
            0.5f,
            0.5f
        );

        maskAdapter.SetBoundingBox(
            bbox,
            Time.realtimeSinceStartupAsDouble,
            FPBBoxCoordinateSpace.NormalizedTopLeft
        );
    }
}