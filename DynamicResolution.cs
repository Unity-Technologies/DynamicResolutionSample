// Render pipelines that utilize the DynamicResolutionHandler class can benefit from a simpler, more streamlined codepath.
// Prefer to leave this defined if possible, comment out only if your render pipeline does not support the feature.
// At the time of this script's last update, HDRP supports the DRH while URP and Built-in do not.
#define PIPELINE_IMPLEMENTS_DRH

// Uncomment this for debugging tools.
// #define ENABLE_DYNAMIC_RESOLUTION_DEBUG

using UnityEngine;
using UnityEngine.Rendering;

public class DynamicResolution : MonoBehaviour
{
    static double DesiredFrameRate = 60.0;
    static double DesiredFrameTime = 1000.0 / DesiredFrameRate;


    // BEGIN TWEAKABLES BLOCK
    const uint ScaleRaiseCounterLimit = 360;

    const uint ScaleRaiseCounterSmallIncrement = 3;
    const uint ScaleRaiseCounterBigIncrement = 10;

    const double HeadroomThreshold = 0.06;
    const double DeltaThreshold = 0.035;

    const float ScaleIncreaseBasis = (HeadroomThreshold < DeltaThreshold) ? (float)HeadroomThreshold : (float)DeltaThreshold;
    const float ScaleIncreaseSmallFactor = 0.25f;
    const float ScaleIncreaseBigFactor = 1.0f;
    const float ScaleHeadroomClampMin = 0.1f;
    const float ScaleHeadroomClampMax = 0.5f;

    const uint NumFrameTimings = 1;

    // If your pipeline utilizes the DRH then the min and max scale factors should be defined by a separate config asset.
    // If not, then these values provide you that configuration.
#if !PIPELINE_IMPLEMENTS_DRH
    const float MinScaleFactor = 0.5f;
    const float MaxScaleFactor = 1.0f;
#endif
    // END TWEAKABLES BLOCK


    // BEGIN INTERNAL TRACKING BLOCK
    uint FrameCount = 0;

    FrameTiming[] FrameTimings = new FrameTiming[NumFrameTimings];

    double GPUFrameTime = 0.0;
    double CPUFrameTime = 0.0;

    double GPUTimeDelta = 0.0;

    uint ScaleRaiseCounter = 0;

    static float CurrentScaleFactor = 1.0f;

    static bool CanUpdate = false;
    static bool SystemEnabled = true;  // Default to false if you plan to init from external settings.
    static bool PlatformSupported = true;

    // These are for an unfortunate hack to work around a current issue, see start of Update for more info.
    // Do not change these unless you are sure about what you are doing.
#if PIPELINE_IMPLEMENTS_DRH
    static bool HasDoneOneTimeInit = false;
    static uint FramesUntilInit = 1;
#endif
    // END INTERNAL TRACKING BLOCK

#if ENABLE_DYNAMIC_RESOLUTION_DEBUG
    static GUIStyle DebugStyle;
#endif


    private void Update()
    {
        if (SystemEnabled)
        {
            // Ideally this logic to set CanUpdate and then conditionally set the scaler would be in Start.
            // Unfortunately, it seems that on app start the first camera Start is called before the DRH instance is properly initialized.
            // Worse, the first Update also happens too soon, hence this logic to do a one time init on the second frame.
            // Subsequent cameras work as expected, so you could do this "the right way" if you worked out start flow to not care about the first camera.
#if PIPELINE_IMPLEMENTS_DRH
            if (!HasDoneOneTimeInit)
            {
                if (FramesUntilInit == 0)
                {
                    CanUpdate = true;
                    DynamicResolutionHandler.SetDynamicResScaler(ScalerDelegate, DynamicResScalePolicyType.ReturnsMinMaxLerpFactor);
                    HasDoneOneTimeInit = true;
                }

                --FramesUntilInit;
            }
#endif

            if (CanUpdate)
            {
                GetFrameStats();

                double headroom = DesiredFrameTime - GPUFrameTime;

                // If headroom is negative, we've exceeded target and need to scale down.
                if (headroom < 0.0)
                {
                    ScaleRaiseCounter = 0;

                    // Since headroom is guaranteed to be negative here, we can add rather than negate and subtract.
                    float scaleDecreaseFactor = (float)(headroom / DesiredFrameTime);
                    CurrentScaleFactor = Mathf.Clamp01(CurrentScaleFactor + scaleDecreaseFactor);

#if !PIPELINE_IMPLEMENTS_DRH
                    SetNewScale();
#endif
                }
                else
                {
                    // If delta is greater than headroom, we expect to exceed target and need to scale down.
                    if (GPUTimeDelta > headroom)
                    {
                        ScaleRaiseCounter = 0;

                        float scaleDecreaseFactor = (float)(GPUTimeDelta / DesiredFrameTime);
                        CurrentScaleFactor = Mathf.Clamp01(CurrentScaleFactor - scaleDecreaseFactor);

#if !PIPELINE_IMPLEMENTS_DRH
                        SetNewScale();
#endif
                    }
                    else
                    {
                        // If delta is negative, then perf is moving in a good direction and we can increment to scale up faster.
                        if (GPUTimeDelta < 0.0)
                        {
                            ScaleRaiseCounter += ScaleRaiseCounterBigIncrement;
                        }
                        else
                        {
                            double headroomThreshold = DesiredFrameTime * HeadroomThreshold;
                            double deltaThreshold = DesiredFrameTime * DeltaThreshold;

                            // If we're too close to target or the delta is too large, do nothing out of concern that we could scale up and exceed target.
                            // Otherwise, slow increment towards a scale up.
                            if ((headroom > headroomThreshold) && (GPUTimeDelta < deltaThreshold))
                            {
                                ScaleRaiseCounter += ScaleRaiseCounterSmallIncrement;
                            }
                        }

                        if (ScaleRaiseCounter >= ScaleRaiseCounterLimit)
                        {
                            ScaleRaiseCounter = 0;

                            // Headroom as percent of target is unlikely to use the full 0-1 range, so clamp on user settings and then remap to 0-1.
                            float headroomPercent = (float)(headroom / DesiredFrameTime);
                            float clampedHeadroom = Mathf.Clamp(headroomPercent, ScaleHeadroomClampMin, ScaleHeadroomClampMax);
                            float remappedHeadroom = (clampedHeadroom - ScaleHeadroomClampMin) / (ScaleHeadroomClampMax - ScaleHeadroomClampMin);
                            float scaleIncreaseFactor = ScaleIncreaseBasis * Mathf.Lerp(ScaleIncreaseSmallFactor, ScaleIncreaseBigFactor, remappedHeadroom);
                            CurrentScaleFactor = Mathf.Clamp01(CurrentScaleFactor + scaleIncreaseFactor);

#if !PIPELINE_IMPLEMENTS_DRH
                            SetNewScale();
#endif
                        }
                    }
                }
            }
        }
#if ENABLE_DYNAMIC_RESOLUTION_DEBUG
        else if (PlatformSupported)
        {
            // Still report frame stats when debug is enabled for platforms that support the feature
            GetFrameStats();
        }
#endif
    }

    // For pipelines with the DRH, we set a 0-1 scaler and we're done.
    // Otherwise, we need to remap our range and call the resize ourselves.
#if PIPELINE_IMPLEMENTS_DRH
    static private float ScalerDelegate()
    {
        return CurrentScaleFactor;
    }
#else
    private void SetNewScale()
    {
        float finalScaleFactor = Mathf.Lerp(MinScaleFactor, MaxScaleFactor, CurrentScaleFactor);
        ScalableBufferManager.ResizeBuffers(finalScaleFactor, finalScaleFactor);
    }
#endif

    static private void ResetScale()
    {
        CurrentScaleFactor = 1.0f;

#if !PIPELINE_IMPLEMENTS_DRH
        // The DRH does this for us, need to do it manually if we're in the path where it isn't in use.
        // Otherwise some targets don't get sized properly between cameras (like with level transitions).
        ScalableBufferManager.ResizeBuffers(MaxScaleFactor, MaxScaleFactor);
#endif
    }

    private void GetFrameStats()
    {
        if (FrameCount < NumFrameTimings)
        {
            ++FrameCount;

            return;
        }

        FrameTimingManager.CaptureFrameTimings();
        FrameTimingManager.GetLatestTimings(NumFrameTimings, FrameTimings);

        if (FrameTimings.Length < NumFrameTimings)
        {
            return;
        }

        // On the rare occasion that this happens, throw away data because we can't trust the frame's timings.
        if (FrameTimings[0].cpuTimeFrameComplete < FrameTimings[0].cpuTimePresentCalled)
        {
            return;
        }

        // This should only be 0 if we haven't previously collected frame data, making delta calc invalid.
        if (GPUFrameTime != 0.0)
        {
            GPUTimeDelta = FrameTimings[0].gpuFrameTime - GPUFrameTime;
        }

        GPUFrameTime = FrameTimings[0].gpuFrameTime;
        CPUFrameTime = FrameTimings[0].cpuFrameTime;
    }

    static public void Enable()
    {
        if (PlatformSupported)
        {
            SystemEnabled = true;
        }
    }

    static public void Disable()
    {
        if (PlatformSupported)
        {
            SystemEnabled = false;

            ResetScale();
        }
    }

    static public bool IsSupportedOnPlatform()
    {
        return PlatformSupported;
    }

    static public bool IsEnabled()
    {
        return SystemEnabled;
    }

    static public double GetTargetFramerate()
    {
        return DesiredFrameRate;
    }

    static public void SetTargetFramerate(double target)
    {
        DesiredFrameRate = target;
        DesiredFrameTime = 1000.0 / target;

        ResetScale();
    }

    private void Start()
    {
        // Metal on iOS will fail the timer frequency check, but we know it works so skip the check in that case.
#if UNITY_IOS
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Metal)
#endif
        {
            // If either of these report zero it means the platform doesn't support dynamic resolution.
            if (FrameTimingManager.GetCpuTimerFrequency() == 0 || FrameTimingManager.GetGpuTimerFrequency() == 0)
            {
                PlatformSupported = false;
                SystemEnabled = false;
            }
        }

#if !PIPELINE_IMPLEMENTS_DRH
        CanUpdate = true;
#endif

#if ENABLE_DYNAMIC_RESOLUTION_DEBUG
        if (DebugStyle == null)
        {
            DebugStyle = new GUIStyle();
        }
#endif
    }

    private void OnDestroy()
    {
        if (SystemEnabled)
        {
            ResetScale();
        }
    }

#if ENABLE_DYNAMIC_RESOLUTION_DEBUG
    private void OnGUI()
    {
        int rezWidth = (int)Mathf.Ceil(ScalableBufferManager.widthScaleFactor * Screen.width);
        int rezHeight = (int)Mathf.Ceil(ScalableBufferManager.heightScaleFactor * Screen.height);
        float curScale = ScalableBufferManager.widthScaleFactor;

        DebugStyle = GUI.skin.box;
        DebugStyle.fontSize = 20;
        DebugStyle.alignment = TextAnchor.MiddleLeft;

        GUILayout.Label(
            string.Format(
                "Enabled: {0}\nResolution: {1} x {2}\nScaleFactor: {3:F3}\nGPU: {4:F3} CPU: {5:F3}",
                SystemEnabled,
                rezWidth,
                rezHeight,
                curScale,
                GPUFrameTime,
                CPUFrameTime),
            DebugStyle);
    }
#endif
}
