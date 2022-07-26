# DynamicResolutionSample

A simple, game usable script to drive Unity's dynamic resolution implementation.

## What Is This?

Dynamic resolution is a technique for scaling render targets to reduce the number of pixels being processed to adapt to GPU performance concerns.  It works by allocating the target resources at full resolution and then aliasing scaled versions over the same memory, which is very lightweight on the platforms/graphics APIs where it's supported.  The final present target is unscaled so an upscale blit will always have to happen somewhere in the pipeline before that point.

Unity has support for dynamic resolution in all render pipelines but does not implement scaling logic internally.  This has the advantage of allowing each game to custom tailor their approach to their specific needs.  But that means that each studio needs to discover this is an available option and figure out how to implement it.

The purpose of this repository is to provide a simple, easy to understand script that could be used immediately as a drop-in solution or as the basis for a customized implementation.

## How Do I Use it?

First, make sure that your project is set up to allow for dynamic resolution.  This means checking the **Allow Dynamic Resolution** box on every camera you want to be scaled (described [here](https://docs.unity3d.com/Manual/DynamicResolution.html)), and in the case of HDRP also configuring it in your HDRP asset (described [here](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@latest?subfolder=/manual/Dynamic-Resolution.html)).  Since this sample utilizes the FrameTimingManager, also check the **Frame Timing Stats** checkbox in player settings for each desired platform.

Second, drop the DynamicResolution.cs file from this repository into your project and attach it to something in the scene.  The single rule here is that you only want one instance of this script running at any given time.  Breaking this rule should have no functional effect on scaling results but will cause unneeded time to be wasted in collecting the timing data mutliple times per frame.

And for basic usage, that should be it!  At this point, your game should be dynamically scaling render targets on supported platforms.

*Note:  Dynamic resolution is only available on some combinations of platform/graphics API.  If you are not seeing dynamic resolution working as expected, make sure you're using a supported configuration.  Dynamic resolution generally does not function in the editor, so make sure that you're testing standalone builds.*

## Basic Configuration ##

At the very top of the script is a line that contains ```#define PIPELINE_IMPLEMENTS_DRH```.  While all pipelines support dynamic resolution they do not all use the DynamicResolutionHandler and you want to comment out this line for cases that do not.  The built-in renderer is one case that does not, as the DynamicResolutionHandler is part of the SRP Core package.  At the time of this writing, URP is also a case that does not.  Commenting out this line and not utilizing the DynamicResolutionHandler will be referred to as *the fallback path* for this rest of this document.

The Enable and Disable functions are always available, but by default this script starts in an enabled state when deployed to supported platforms.  If you'd like to change this behavior so frame scaling doesn't happen until you've explicitly called Enable, simply change the default value of ```SystemEnabled``` to false.

Likewise, the script is written to target 60 fps initially.  You can change the default value of ```DesiredFrameRate``` to start your title at a different target framerate, or start with scaling disabled and call ```SetTargetFramerate``` before enabling.  After initialization, target framerate can freely be changed.

If your title is using the fallback path, then you need to set the minimum and maximum bounds of resolution scaling (these are configured by a settings asset when the DynamicResolutionHandler is utilized).  In the script these can be found as ```MinScaleFactor``` and ```MaxScaleFactor```.  Both should be values in the 0 - 1 range, and min should be lower than max.

## Advanced Configuration And More Information ##

The core concept behind dynamic resolution is to attempt to maximize frame resolution while maintaining desired performance.  This script does that, but is skewed primarily towards ensuring that the title does not miss a vblank.  That means that it scales down very aggressively but scales back up conservatively, both in terms of speed and amount of each scale up.  How conservative that scale up should be can vary from title to title, so most of the tweakables are geared towards this aspect of the logic.

### Scaling Logic Flow ###

* Get current timings, calculate headroom as the difference between desired frametime and currently reported GPU time.
* If headroom is negative:
	* Frame is already long, scale down by headroom / desired frametime.
* Else, we've got some room to spare.
	* If the GPU time delta is greater than headroom:
		* We're about to go long, scale down by GPU time delta / desired frametime.
	* Else, we expect to stay within target at current scale.
		* If GPU time delta is negative:
			* Workload is going down, so increment the counter for scale up by a large amount.
		* Else:
			* If available headroom is greater than threshold and the GPU time delta is less than threshold:
				* We've got room to work with, but only increment the counter by a small amount.
		* If the scale up counter has hit target:
			* Scale up using an adaptive range where we take a larger step the further away we are from target.

### Understanding The Tweakables And Tuning Your Game ###

#### ScaleRaiseCounter Group ####

While scale down is instantaneous, scale up is designed to happen only after enough frames have occurred where available headroom and/or time deltas are in an acceptable range.  To accomplish this, an internal counter is kept which can increment by different amounts depending on the performance characteristics and once a target count is hit will trigger a scale up.  A change in scale in either direction will reset the counter.

```ScaleRaiseCounterLimit``` - This is the target count that, once hit by the internal counter, will trigger a scale up.  By default it is set to 360.<br>
```ScaleRaiseCounterBigIncrement``` - If the time delta is negative then the current performance trend is very good and we can increment the internal counter by this larger value.  By default it is set to 10.<br>
```ScaleRaiseCounterSmallIncrement``` - The normal increment amount.  By default it is set to 3.

The defaults have been set such that in an average case it would take 120 frames between scale up operations, while in the best case it would take 36 frames.  This translates to 2 seconds or 0.6 seconds between scale ups, respectively, at 60 fps.  The separation of small vs big increment is to allow individual tuning based on the variance of GPU workload in a title.

#### Threshold Group ####

In the situation where the script might perform the small counter increment, there are two conditions where no increment will happen; if either the available headroom is too small or the time delta is too large.

```HeadroomThreshold``` - Defines a minimum fraction of desired frametime that must be available to consider a scale up.  By default it is set to 0.06, or 6% of target.<br>
```DeltaThreshold``` - Defines a maximum fraction of desired frametime that the current GPU delta can be to consider a scale up.  By default it is set to 0.035, or 3.5% of target.

The defaults have been set such that a title needs more than 1 ms of headroom and time delta must be less than 0.5833 ms at 60 fps to trigger a small counter increment towards a scale up.  The headroom control is designed to be tuned against how likely a single spike is to be large enough to push a title over target framerate, whereas the delta control is for when there could be plenty of headroom but the nature of spikes makes it hard to predict how accurate our heuristic is going to be at staying within the vblank.

#### ScaleIncrease and ScaleHeadroomClamp Group ####

Once a scale up has been triggered the script then needs to determine how much to scale up by.  While scale down always happens by whatever amount is necessary to stay within target, scale up happens in much smaller chunks, both to conservatively ensure we don't increase beyond target and to minimize how visually noticeable the change is.  The size of the scale up is designed to vary based on how much available headroom there is.

```ScaleIncreaseBasis``` - The starting point for the scale up amount.  By default it is the lesser of the two threshold controls.<br>
```ScaleHeadroomClampMin``` - The lower bound of the headroom clamp and range remap, it should be set to a minimum of 0 and less than the clamp max.  By default it is set to 0.1.<br>
```ScaleHeadroomClampMax``` - The upper bound of the headroom clamp and range remap, it should be set to great than the clamp min and a maximum of 1.  By default it is set to 0.5.<br>
```ScaleIncreaseSmallFactor``` - The minimum multiplier against the remapped scale increase factor.  By default it is set to 0.25.<br>
```ScaleIncreaseBigFactor``` - The maximum multiplier against the remapped scale increase factor.  By default it is set to 1.

The scale up amount logic starts by determining available headroom as a fraction of target frametime.  This value will be in the 0 - 1 range but no title will see that entire range used, so the headroom clamp controls allow each title to tune the viable range they want to act upon.  The defaults clamp out the bottom 10% and top 50% of the range, which at 60 fps corresponds to 8.33 ms being the maximum and 1.66 ms being the minimum of the sliding scale window.

Next, this value is remapped from the clamped range back to the 0 - 1 range, and this becomes the coefficient in a lerp between the small and big increase factors.  This remap ensures that the small and big endpoints are directly used and meaningful during the tuning process.  The defaults are set so that the scale window will slide between full and quarter rate increases depending on the available headroom.

The result of the lerp is multiplied into the original increase basis value to determine the final scale up amount.  The basis was chosen as the lesser of the thresholds as they represent sufficiently small slices of target frametime so increases by some portion of them should never push the title beyond the target.

## Debugging ##

Uncomment the line near the top with ```#define ENABLE_DYNAMIC_RESOLUTION_DEBUG``` to get a small debug window in the top left of the game window with information on if dynamic resolution is currently running, what the current resolution is, what the scale factor is, and the currently reported CPU and GPU times.
