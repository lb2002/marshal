using System;
using TMPro;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>The start panel and the closing card: glass panels with pill buttons, appearing with a short scale and fade.</summary>
    public static class Panels
    {
        const float Width = 0.36f;
        static readonly Vector2 BigButton = new Vector2(0.115f, Theme.H);
        static readonly Vector2 SmallButton = new Vector2(0.07f, Theme.H);

        /// <summary>
        /// "Lotse", "Deine KI-Agenten am Schreibtisch", two large pills ("Demo ansehen (Aufnahme)"; without `onStart`,
        /// when the demo pack could not be loaded, a disabled "Demo nicht verfügbar"; and "Mit meinem PC verbinden",
        /// disabled without `onBridge`), a small "Neu platzieren" (`onReplace`; so placing again never depends on the wrist
        /// menu) and a one-line hint about the gestures; standing on localPos (its bottom edge), so the banner below it
        /// never overlaps it. (U14) With `onTour`, "Take the tour" is the primary button, alone in the first row, and the
        /// demo's button is a quiet one.
        /// </summary>
        public static GameObject Start(Transform parent, Vector3 localPos, Action onStart, Action onBridge = null, Action onReplace = null,
            Action onTour = null)
        {
            var root = Ui.Child(parent, "StartPanel", localPos);
            var motion = Ui.Child(root.transform, "Motion", Vector3.zero);
            var c = Ui.Child(motion.transform, "Content", Vector3.zero).transform;
            // U13: small "Marshal" in muted, the headline, the two buttons side by side and "Place again" as a text link
            const float U = Theme.U;
            var left = -Width * 0.5f;
            var y = -34f * U;
            var mark = Ui.Text(c, "Title", Texts.StartTitle, Vector3.zero, Width, Theme.Small, Theme.Muted, TextAlignmentOptions.Left);
            mark.font = Fonts.Medium;
            Ui.Middle(mark, Width, 19f * U, TextAlignmentOptions.Left);
            mark.transform.localPosition = new Vector3(0f, y - 9.5f * U, -0.001f);
            y -= 27f * U;
            var say = Ui.Text(c, "Headline", Texts.StartHeadline, Vector3.zero, Width, Theme.Headline, Theme.Text);
            say.font = Fonts.SemiBold;
            say.characterSpacing = 100f * Taxi.HeadlineTracking;
            // balanced: the narrowest measure that still gives two lines
            say.ForceMeshUpdate();
            if (say.textInfo.lineCount == 2)
            {
                float lo = Width * 0.4f, hi = Width;
                for (var i = 0; i < 8; i++)
                {
                    var mid = (lo + hi) * 0.5f;
                    say.rectTransform.sizeDelta = new Vector2(mid, 0.01f);
                    say.ForceMeshUpdate();
                    if (say.textInfo.lineCount <= 2) hi = mid;
                    else lo = mid;
                }
                say.rectTransform.sizeDelta = new Vector2(hi, 0.01f);
            }
            var lines = Ui.Rows(say, 44f * 1.04f * U, out var shift);
            say.transform.localPosition = new Vector3(left + say.rectTransform.sizeDelta.x * 0.5f, y + shift, -0.001f);
            y -= lines * 44f * 1.04f * U + 30f * U;
            // (U14) the tour first and alone, as the primary button; the demo and the PC under it, quiet
            if (onTour != null)
            {
                PokeButton.Create(c, Texts.TourButton, new Vector3(left + BigButton.x * 0.5f, y - BigButton.y * 0.5f, -0.002f), BigButton, onTour,
                    primary: true);
                y -= BigButton.y + Theme.ControlGap;
            }
            var x = left;
            PokeButton.Create(c, onStart != null ? Texts.StartButton : Texts.DemoUnavailable, new Vector3(x + BigButton.x * 0.5f, y - BigButton.y * 0.5f, -0.002f),
                BigButton, onStart, interactable: onStart != null, primary: onTour == null);
            x += BigButton.x + Theme.ControlGap;
            PokeButton.Create(c, Texts.BridgeButton, new Vector3(x + BigButton.x * 0.5f, y - BigButton.y * 0.5f, -0.002f), BigButton, onBridge,
                interactable: onBridge != null);
            x += BigButton.x + Theme.ControlGap;
            if (onReplace != null)
                PokeButton.Create(c, Texts.Replace, new Vector3(left + Width - SmallButton.x * 0.5f, y - SmallButton.y * 0.5f, -0.002f), SmallButton, onReplace,
                    kind: ButtonKind.Text);
            y -= BigButton.y + 22f * U;
            var hint = Ui.Text(c, "Hint", Texts.StartHint, Vector3.zero, Width, Theme.Small, Theme.Muted, TextAlignmentOptions.Left);
            Ui.Middle(hint, Width, 19f * U, TextAlignmentOptions.Left);
            hint.transform.localPosition = new Vector3(0f, y - 9.5f * U, -0.001f);
            y -= 19f * U + 30f * U;
            Background(c, y, standOnOrigin: true);
            Appear.Play(motion);
            return root;
        }

        /// <summary>The closing card with the summary sentence and "Nochmal"; centred on localPos.</summary>
        public static GameObject End(Transform parent, Vector3 localPos, string summary, Action onAgain)
        {
            var root = Ui.Child(parent, "EndCard", localPos);
            var motion = Ui.Child(root.transform, "Motion", Vector3.zero);
            var c = Ui.Child(motion.transform, "Content", Vector3.zero).transform;
            var y = -Theme.Pad;
            var title = Ui.Text(c, "Title", "Demo finished", new Vector3(0f, y, -0.001f), Width, Theme.Title, Theme.Text,
                TextAlignmentOptions.Top, richText: true);
            y -= title.rectTransform.sizeDelta.y + 2f * Theme.S;
            var text = Ui.Text(c, "Summary", summary, new Vector3(0f, y, -0.001f), Width, Theme.Body, Theme.Text,
                TextAlignmentOptions.Top);
            y -= text.rectTransform.sizeDelta.y + 4f * Theme.S;
            PokeButton.Create(c, Texts.Again, new Vector3(0f, y - SmallButton.y * 0.5f, -0.002f), new Vector2(0.115f, Theme.H), onAgain, primary: true);
            y -= SmallButton.y + Theme.Pad;
            Background(c, y, standOnOrigin: false);
            Appear.Play(motion);
            return root;
        }

        /// <summary>The background, then the panel moved to stand on its origin or to be centred on it.</summary>
        static void Background(Transform c, float bottom, bool standOnOrigin)
        {
            var h = -bottom;
            Ui.Panel(c, "Background", new Vector3(0f, -h * 0.5f, 0.002f), new Vector2(Width + 80f * Theme.U, h), Ui.PanelColor);
            var lift = standOnOrigin ? h : h * 0.5f;
            foreach (Transform child in c) child.localPosition += new Vector3(0f, lift, 0f);
        }
    }
}
