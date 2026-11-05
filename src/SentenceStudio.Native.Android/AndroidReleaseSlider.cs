using Android.Runtime;
using AndroidX.Compose;
using AndroidX.Compose.Material3;
using AndroidX.Compose.Runtime;
using AndroidX.Compose.UI;
using AndroidX.Compose.UI.Platform;
using AndroidX.Compose.UI.Semantics;
using Kotlin.Jvm.Functions;

namespace SentenceStudio.Native.Android;

/// <summary>
/// The Material 3 Compose <c>Slider</c> with <c>onValueChangeFinished</c>, which the
/// facade <see cref="Slider"/> (Microsoft.AndroidX.Compose 0.1.0-foundation.999911b.1,
/// <c>ComposeBridges.Slider</c>) always passes as null. Material 3 1.4 calls it when a
/// drag or tap is released, on a D-pad key up and after an accessibility set-progress.
/// This node calls the same bound <c>SliderKt.Slider</c> overload the facade calls,
/// adding only that callback; colors, interaction source, thumb and track keep their
/// Kotlin defaults. The facade cannot hand a <see cref="ComposableNode.Modifier"/> to a
/// direct call (its conversion is internal), so the node builds the slider's own
/// modifier (test tag, then accessible name) as the facade would, keeping both on the
/// slider's accessibility node. Remove once the facade Slider exposes the callback.
/// </summary>
internal sealed class AndroidReleaseSlider(
    float value,
    Action<float> onValueChange,
    Action onValueChangeFinished,
    FloatRange valueRange,
    bool enabled,
    int steps,
    string testTag,
    string contentDescription) : ComposableNode
{
    // SliderKt.Slider $default bits (facade ComposeDefaults "SliderDefault": value,
    // onValueChange, modifier, enabled, onValueChangeFinished, colors, interactionSource,
    // steps, thumb, track, valueRange): colors, interactionSource, thumb and track.
    internal const int KotlinDefaults = (1 << 5) | (1 << 6) | (1 << 8) | (1 << 9);

    private static IntPtr s_modifierCompanion;

    public override void Render(IComposer composer)
    {
        // One callback peer per slot, retargeted each render, as the facade's RememberAction does.
        var change = composer.Remember(() => new FloatCallback());
        change.Target = onValueChange;
        var finished = composer.Remember(() => new UnitCallback());
        finished.Target = onValueChangeFinished;
        // A new semantics lambda each render, so a changed label invalidates the node.
        var modifier = SemanticsModifierKt.Semantics(
            TestTagKt.TestTag(EmptyModifier(), testTag),
            mergeDescendants: false,
            new ContentDescriptionCallback(contentDescription));
        using var range = Kotlin.Ranges.RangesKt.RangeTo(valueRange.Start, valueRange.End);
        // Bound names are shifted: p7 is steps; steps, _changed, _changed1 are
        // $changed, $changed1 and $default.
        SliderKt.Slider(
            value: value,
            onValueChange: change,
            modifier: modifier,
            enabled: enabled,
            onValueChangeFinished: finished,
            colors: null,
            interactionSource: null,
            p7: steps,
            thumb: null,
            track: null,
            valueRange: range,
            _composer: composer,
            steps: 0,
            _changed: 0,
            _changed1: KotlinDefaults);
    }

    /// <summary>
    /// Kotlin's <c>Modifier</c> (the companion object), read the way the facade's
    /// <c>ComposeBridges.ModifierCompanionInstance</c> reads it: the binding does not
    /// expose the outer interface's <c>Companion</c> field.
    /// </summary>
    private static IModifier EmptyModifier()
    {
        if (s_modifierCompanion == IntPtr.Zero)
        {
            var type = JNIEnv.FindClass("androidx/compose/ui/Modifier");
            try
            {
                var field = JNIEnv.GetStaticFieldID(type, "Companion",
                    "Landroidx/compose/ui/Modifier$Companion;");
                var local = JNIEnv.GetStaticObjectField(type, field);
                if (local == IntPtr.Zero)
                    throw new InvalidOperationException("Compose Modifier.Companion was not initialized.");
                s_modifierCompanion = JNIEnv.NewGlobalRef(local);
                JNIEnv.DeleteLocalRef(local);
            }
            finally
            {
                JNIEnv.DeleteGlobalRef(type);
            }
        }
        return Java.Lang.Object.GetObject<IModifier>(s_modifierCompanion, JniHandleOwnership.DoNotTransfer)
            ?? throw new InvalidOperationException("Compose Modifier.Companion could not be wrapped.");
    }

    private sealed class FloatCallback : Java.Lang.Object, IFunction1
    {
        internal Action<float>? Target;

        public Java.Lang.Object Invoke(Java.Lang.Object? p0)
        {
            if (p0 is Java.Lang.Float raw)
                Target?.Invoke(raw.FloatValue());
            return Kotlin.Unit.Instance!;
        }
    }

    private sealed class UnitCallback : Java.Lang.Object, IFunction0
    {
        internal Action? Target;

        public Java.Lang.Object Invoke()
        {
            Target?.Invoke();
            return Kotlin.Unit.Instance!;
        }
    }

    private sealed class ContentDescriptionCallback(string description) : Java.Lang.Object, IFunction1
    {
        public Java.Lang.Object Invoke(Java.Lang.Object? p0)
        {
            if (p0 is not null)
                SemanticsPropertiesKt.SetContentDescription(
                    p0.JavaCast<ISemanticsPropertyReceiver>(), description);
            return Kotlin.Unit.Instance!;
        }
    }
}
