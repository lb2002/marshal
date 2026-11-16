using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>What an agent's sign on the rail is.</summary>
    public enum RailSign
    {
        /// <summary>A white dot: working.</summary>
        Dot,
        /// <summary>Three small red lights: it holds for the user, or everything is stopped.</summary>
        Lights,
        /// <summary>A green dot: landed.</summary>
        Landed,
    }

    /// <summary>One sign of the rail.</summary>
    public readonly struct RailSlot
    {
        public readonly string Sid;
        public readonly RailSign Sign;

        public RailSlot(string sid, RailSign sign)
        {
            Sid = sid;
            Sign = sign;
        }
    }

    /// <summary>
    /// The folded window (U6; U13's look): a narrow asphalt pill, 68 units wide and as high as its signs need, its top
    /// on the window's top edge, at the window's edge next to the display. One sign per agent in the list's order (at
    /// most 7, then "+2"): a white dot (working), three small red lights (holding for the user, or stopped), a green
    /// dot (landed). All signs are one mesh. A poke or ray click on a sign opens that agent's row; on the rest of the
    /// rail it opens the window as it was. It never opens by itself and never by gaze. Origin: the window's top left
    /// corner.
    /// </summary>
    public sealed class RailView : MonoBehaviour
    {
        const float U = Theme.U;
        public const float Width = 68f * U;
        public const float Radius = 22f * U;
        const float Pad = 18f * U;
        const float Sign = 16f * U;
        /// <summary>From one sign's middle to the next.</summary>
        public const float SlotPitch = 38f * U;
        /// <summary>The rail is never lower than this (one sign and its padding).</summary>
        public const float MinHeight = 2f * Pad + Sign;

        float _height;
        readonly PokeButton[] _areas = new PokeButton[RailSlots.MaxOrbs];
        readonly string[] _sids = new string[RailSlots.MaxOrbs];
        readonly RailSign[] _signs = new RailSign[RailSlots.MaxOrbs];
        FlatMesh _mesh;
        TextMeshPro _extra;
        string _key;

        public PokeButton Area { get; private set; }
        /// <summary>"+2" when there are more agents than signs, else "".</summary>
        public string CountsShown => _extra.gameObject.activeSelf ? _extra.text : "";
        public int OrbsShown { get; private set; }

        /// <summary>Its height now: what its signs need.</summary>
        public float Height => _height;

        /// <summary>The height `signs` signs need (and one more line for "+2"). Pure.</summary>
        public static float HeightFor(int signs, bool extra)
        {
            if (signs <= 0) return MinHeight;
            return 2f * Pad + Sign + (signs - 1 + (extra ? 1 : 0)) * SlotPitch;
        }

        /// <summary>The sign of `sid` as drawn, or null when it is not on the rail.</summary>
        public RailSign? SignOf(string sid)
        {
            for (var i = 0; i < OrbsShown; i++)
                if (_sids[i] == sid) return _signs[i];
            return null;
        }

        public PokeButton AreaOf(string sid)
        {
            for (var i = 0; i < _areas.Length; i++)
                if (_sids[i] == sid && _areas[i].gameObject.activeSelf) return _areas[i];
            return null;
        }

        /// <summary>The rail's centre, local (the gaze timer aims at it).</summary>
        public Vector3 LocalCentre => transform.localPosition + new Vector3(Width * 0.5f, -_height * 0.5f, 0f);

        public static RailView Create(Transform parent, Vector3 localTopLeft, Action onRail, Action<string> onSign)
        {
            var v = Ui.Child(parent, "Rail", localTopLeft).AddComponent<RailView>();
            v._height = MinHeight;
            v.Area = PokeButton.CreateArea(v.transform, "Rail", new Vector3(Width * 0.5f, -MinHeight * 0.5f, -0.003f), new Vector2(Width, MinHeight), onRail);
            // the whole rail is the 3-degree target; a sign is a finer one inside it
            v.Area.HitPad = new Vector2(Theme.HitPad, Theme.HitPad);
            for (var i = 0; i < RailSlots.MaxOrbs; i++)
            {
                var index = i;
                v._areas[i] = PokeButton.CreateArea(v.transform, "Rail agent " + (i + 1), new Vector3(Width * 0.5f, SlotY(i), -0.004f),
                    new Vector2(Width, Sign + 10f * U), () =>
                    {
                        var sid = v._sids[index];
                        if (sid != null) onSign?.Invoke(sid);
                    });
                v._areas[i].HitPad = Vector2.zero;
                v._areas[i].gameObject.SetActive(false);
            }
            v._extra = Ui.Text(v.transform, "Extra", "", Vector3.zero, Width, Theme.Small, Theme.Muted, TextAlignmentOptions.Center);
            Ui.Middle(v._extra, Width, Sign, TextAlignmentOptions.Center);
            v._extra.gameObject.SetActive(false);
            v._mesh = new FlatMesh(v.transform, "Signs", -0.0005f);
            return v;
        }

        static float SlotY(int i) => -Pad - Sign * 0.5f - i * SlotPitch;

        /// <summary>The signs of all agents (at most 7 shown, then "+n").</summary>
        public void Show(IReadOnlyList<RailSlot> slots)
        {
            var n = RailSlots.Shown(slots.Count);
            OrbsShown = n;
            var key = new System.Text.StringBuilder();
            for (var i = 0; i < _areas.Length; i++)
            {
                var on = i < n;
                var sid = on ? slots[i].Sid : null;
                if (sid != _sids[i]) _areas[i].ResetPress();
                _sids[i] = sid;
                if (on) _signs[i] = slots[i].Sign;
                if (_areas[i].gameObject.activeSelf != on) _areas[i].gameObject.SetActive(on);
                if (on) key.Append(sid).Append(':').Append((int)slots[i].Sign).Append('|');
            }
            var extra = RailSlots.Extra(slots.Count);
            key.Append(extra);
            SetHeight(HeightFor(n, extra.Length > 0));
            var k = key.ToString();
            if (k == _key) return;
            _key = k;
            _extra.gameObject.SetActive(extra.Length > 0);
            if (extra.Length > 0)
            {
                _extra.text = extra;
                _extra.transform.localPosition = new Vector3(Width * 0.5f, SlotY(n), -0.001f);
            }
            var m = _mesh;
            m.Clear();
            for (var i = 0; i < n; i++)
            {
                var c = new Vector2(Width * 0.5f, SlotY(i));
                if (_signs[i] == RailSign.Lights)
                {
                    // three small red lights, with the one glow the design has
                    float w = 22f * U, h = 7f * U, gap = 4f * U;
                    var total = 3f * h + 2f * gap;
                    var column = new Rect(c.x - w * 0.5f, c.y - total * 0.5f, w, total);
                    var glow = FlatMesh.Over(Look.Stop, 0.20f, Look.Asphalt);
                    m.RoundedRect(column, Theme.LightRadius, glow, 2f * FlatMesh.Layer);
                    m.Halo(column, Theme.LightRadius, 5f * U, glow, FlatMesh.Solid(Look.Asphalt), 2f * FlatMesh.Layer);
                    for (var j = 0; j < 3; j++)
                        m.RoundedRect(new Rect(column.x, column.yMax - h - j * (h + gap), w, h), Theme.LightRadius, FlatMesh.Solid(Look.Stop));
                }
                else m.Disc(c, Sign * 0.5f, FlatMesh.Solid(_signs[i] == RailSign.Landed ? Look.Go : Look.Ink));
            }
            m.Apply();
        }

        /// <summary>The rail's height (its area follows; the window's background follows Height).</summary>
        void SetHeight(float height)
        {
            if (Mathf.Approximately(height, _height)) return;
            _height = height;
            Area.Resize(new Vector2(Width, height));
            Area.transform.localPosition = new Vector3(Width * 0.5f, -height * 0.5f, -0.003f);
        }
    }
}
