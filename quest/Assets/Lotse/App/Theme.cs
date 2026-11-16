using UnityEngine;
using UnityEngine.Rendering;

namespace Lotse.App
{
    /// <summary>
    /// The one look (U13, "taxi lines"): an airfield at night. One opaque asphalt surface #17191C, no cards and no
    /// borders; only what needs the user is raised (#212428); insets #0E0F11 (the command, the reply field); ink
    /// #F3F1EA, muted #8F9296, faint #3B3F45; taxi yellow for the way done, stop red for what holds, go green for what
    /// landed. Five radii (window 30, raised row 16, button 12, inset 10, light 3 units), not one. Three kinds of
    /// button: quiet (white at 7 %), primary (ink), deny (a red outline). Sizes are the design's units (1180 across the
    /// window: Theme.U metres each). One material per style, shared by every renderer of it; colours, fade and
    /// highlight go per renderer through a MaterialPropertyBlock (no per-frame allocation). `Still` (the "Calm"
    /// setting, and always while a hold runs): then nothing moves.
    /// </summary>
    public static class Theme
    {
        static Color Hex(int rgb, float a = 1f) => Ui.ToColor(Look.Hex(rgb), a);

        /// <summary>
        /// White at `a` as the mock-up means it: drawn over the graphite panel, a browser blends in sRGB, the headset
        /// in linear light, where the same alpha reads far brighter (6 % white over graphite looks like 20 %). The
        /// alpha here gives the mock-up's result in linear light.
        /// </summary>
        static Color White(float a) => new Color(1f, 1f, 1f, LinearAlpha(1f, Look.Graphite.G, a));

        /// <summary>
        /// The alpha that, blended in linear light, gives what blending `over` on `under` at `a` gives in sRGB (one channel,
        /// sRGB values 0..1). In a gamma project the alpha stays as it is.
        /// </summary>
        public static float LinearAlpha(float over, float under, float a)
        {
            if (QualitySettings.activeColorSpace != ColorSpace.Linear || Mathf.Approximately(over, under)) return a;
            var target = Mathf.GammaToLinearSpace(a * over + (1f - a) * under);
            var lo = Mathf.GammaToLinearSpace(under);
            var hi = Mathf.GammaToLinearSpace(over);
            return Mathf.Clamp01((target - lo) / (hi - lo));
        }

        /// <summary>
        /// The panel's 95 % (U5a fix 1; the brief's 90 % let the monitor show through): over the bright room a browser's 95 %
        /// leaves a twentieth of it; in linear light the same share reads far brighter. 99 % gives that look here.
        /// </summary>
        const float PanelAlpha = 1f;

        // ---- colours ----

        /// <summary>The panel: graphite #1A1F24 at 95 % (see PanelAlpha).</summary>
        public static Color Glass => Ui.ToColor(Look.Graphite, PanelAlpha);
        /// <summary>A panel in front of others (settings, pairing): the same graphite, a little denser.</summary>
        public static Color GlassDense => Ui.ToColor(Look.Graphite, 1f);
        /// <summary>A card on the panel (an agent's column, the approval block, the selected row): one step lighter, opaque.</summary>
        public static Color Card => Ui.ToColor(Look.Card);
        /// <summary>A well inside a card (the command's mono inset): one step darker than the panel.</summary>
        public static Color Well => Ui.ToColor(Look.Well);
        /// <summary>A control at rest (every button that is not the primary one, the text field, a tab's active half).</summary>
        public static Color Control => White(0.07f);
        /// <summary>The one hairline colour: borders and dividers.</summary>
        public static Color Line => Ui.ToColor(Look.Hairline);
        /// <summary>Primary text #ECEFF1.</summary>
        public static Color Text => Ui.ToColor(Look.TextPrimary);
        /// <summary>Secondary text #8A949C.</summary>
        public static Color Muted => Ui.ToColor(Look.TextSecondary);
        /// <summary>Commands, paths and URLs #C9D0D5 (the "mono" style in the font we have).</summary>
        public static Color Mono => Ui.ToColor(Look.TextMono);
        /// <summary>The one primary button per view (Senden, the mic): #ECEFF1 ...</summary>
        public static Color Primary => Ui.ToColor(Look.TextPrimary, 1f);
        /// <summary>... with dark content.</summary>
        public static Color OnPrimary => Ui.ToColor(Look.OnPrimary);
        /// <summary>Every other button: white at 6 % (the chip material adds its hairline).</summary>
        public static Color Secondary => Control;
        /// <summary>A disabled button: fainter than an enabled one.</summary>
        public static Color Disabled => White(0.035f);
        /// <summary>Text on a disabled button.</summary>
        public static Color DisabledText => Ui.ToColor(Look.TextDisabled);
        /// <summary>Text on a bright chip.</summary>
        public static Color OnChip => Ui.ToColor(Look.OnPrimary);
        /// <summary>The selected row: a slightly lighter background.</summary>
        public static Color Selected => Card;
        /// <summary>The approval block: a quiet inset on the panel.</summary>
        public static Color Inset => Card;
        /// <summary>Hairlines: the column divider, the row separators (8 %) ...</summary>
        public static Color Hairline => Line;
        /// <summary>... and the panel's own edge (10 %).</summary>
        public static Color Edge => Line;

        /// <summary>The raw command, path or URL.</summary>
        public static Color Subject => Mono;
        /// <summary>Red text on dark #FF8178: "wartet auf dich", "gefährlich", a two-hand hint.</summary>
        public static Color Attention => Ui.ToColor(Look.RedText);
        /// <summary>The red of shapes (#FF5A4E): the notice's dot, the dangerous hold ring.</summary>
        public static Color Red => Ui.ToColor(Look.Red);
        /// <summary>Green #46C98B: done ("Freigegeben", "Gesendet √").</summary>
        public static Color GoodText => Ui.ToColor(Look.Green);
        /// <summary>A warning in text ("im Terminal prüfen"): the primary colour (no colour but red and green carries meaning).</summary>
        public static Color WarnText => Text;
        /// <summary>An error in the feed.</summary>
        public static Color ErrorText => Ui.ToColor(Look.RedText);
        /// <summary>The empty track of a ring or a bar.</summary>
        public static Color Track => Line;
        /// <summary>The countdown's fill and a safe hold ring.</summary>
        public static Color Fill => Ui.ToColor(Look.TextPrimary, 1f);
        /// <summary>The desk keyboard's board.</summary>
        public static Color KeyboardBase => Ui.ToColor(Look.Graphite, PanelAlpha);

        /// <summary>Banners and the notice pill: the same graphite.</summary>
        public static Color BannerGood => GlassDense;
        public static Color BannerBad => GlassDense;

        // ---- U13 names ----
        public static Color Asphalt => Ui.ToColor(Look.Asphalt);
        public static Color Raised => Ui.ToColor(Look.Raised);
        public static Color Ink => Ui.ToColor(Look.Ink);
        public static Color Faint => Ui.ToColor(Look.Faint);
        public static Color Taxi => Ui.ToColor(Look.Taxi);
        public static Color Stop => Ui.ToColor(Look.Stop);
        public static Color Go => Ui.ToColor(Look.Go);
        /// <summary>The deny button: its label, its outline (stop red at about half strength), its fill under a pointer.</summary>
        public static Color DenyText => Ui.ToColor(Look.StopText);
        public static Color DenyRim => Ui.ToColor(Look.Stop, 0.53f);
        public static Color DenyHover => Ui.ToColor(Look.Stop, 0.11f);
        /// <summary>A text-only control under a pointer: a faint fill.</summary>
        public static Color TextHover => White(0.05f);

        /// <summary>One unit of the design in metres (the window is 1180 units wide).</summary>
        public const float U = UiMetrics.Unit;
        public const float Headline = UiMetrics.Headline;
        public const float Facts = UiMetrics.Facts;
        /// <summary>A button's label (17 units, Medium).</summary>
        public const float Label = UiMetrics.Label;
        public const float WindowRadius = Lotse.App.Taxi.WindowRadius * U;
        public const float RowRadius = Lotse.App.Taxi.RowRadius * U;
        public const float InsetRadius = Lotse.App.Taxi.InsetRadius * U;
        public const float LightRadius = Lotse.App.Taxi.LightRadius * U;

        // ---- typography: four styles and Small, nothing else (U9) ----
        // Title: SemiBold at Title. Body: Regular at Body. Secondary: Body in Muted. Mono: Geist Mono at Body, in Mono.
        // Small: Regular at Small (counts and captions).

        /// <summary>Title (SemiBold): the product name, an agent's title, the block's sentence, panel titles.</summary>
        public const float Title = UiMetrics.Title;
        /// <summary>Body (and Secondary, and Mono): rows, steps, the feed, labels on controls.</summary>
        public const float Body = UiMetrics.Body;
        /// <summary>Small: counts, captions, status lines; the smallest size U4b's renders proved legible.</summary>
        public const float Small = UiMetrics.Small;

        // ---- spacing: one unit S; every gap, padding and margin is 1, 2, 3, 4 or 6 x S (U9) ----

        /// <summary>The spacing unit, 4 mm. 2 x S is the usual inner padding (cards, the block, chips).</summary>
        public const float S = UiMetrics.S;
        /// <summary>2 x S (the pre-U9 name; views not laid out again still use it).</summary>
        public const float Unit = 2f * S;
        /// <summary>The window's own padding and that of free-standing panels: 4 x S.</summary>
        public const float Pad = 4f * S;
        /// <summary>The inner padding of every card: 2 x S.</summary>
        public const float CardPad = 2f * S;
        /// <summary>The one height of every button, tab, text field and menu item.</summary>
        public const float H = UiMetrics.H;
        /// <summary>The invisible hit area around a drawn control for the ray (U10), and the gap between neighbouring controls.</summary>
        public const float HitPad = UiMetrics.HitPad;
        public const float ControlGap = UiMetrics.ControlGap;
        /// <summary>A band inside the window (the header row, a question, the halted band): H with 2 x S above and below.</summary>
        public const float Band = H + 4f * S;
        /// <summary>The height of a status chip (the risk word, the verdict): a label, not a control.</summary>
        public const float Chip = 4f * S;
        /// <summary>One text row of Small or Body text on the grid.</summary>
        public const float Row = 4f * S;
        /// <summary>The one width of hairlines (borders, dividers).</summary>
        public const float Hair = 1.5f * U;
        /// <summary>The radius of cards on a panel (the raised row's).</summary>
        public const float CornerRadius = RowRadius;
        /// <summary>The radius of controls and chips' boxes.</summary>
        public const float ButtonRadius = Lotse.App.Taxi.ButtonRadius * U;
        /// <summary>Orbs: in a column and a list row; in the conversation's header and the rail.</summary>
        public const float OrbSmall = 0.018f;
        public const float OrbLarge = 0.024f;

        /// <summary>Nothing moves: the "Ruhig" setting, and always while a hold runs (AppRoot sets it every frame).</summary>
        public static bool Still { get; set; }

        /// <summary>The "Ruhig" setting itself: the orbs then stand in their still pose (rings standing, badges shown).</summary>
        public static bool Calm { get; set; }

        // ---- materials, one per style ----

        static Material _glass;
        static Material _chip;
        static Material _glow;

        /// <summary>Panels: rounded graphite glass with a hairline edge and one soft shadow, depth written.</summary>
        public static Material GlassMaterial =>
            _glass != null ? _glass : _glass = ShapeMaterial("Lotse Glass", Ui.QueuePanel, WindowRadius, rim: 0f, shadow: 0.34f,
                shadowSize: 0.016f, shadowOffset: 0.4f);

        /// <summary>Buttons, insets and the selection on a panel: a rounded box with a hairline, no shadow.</summary>
        public static Material ChipMaterial
        {
            get
            {
                if (_chip != null) return _chip;
                _chip = ShapeMaterial("Lotse Chip", Ui.QueueOnPanel, ButtonRadius, rim: 0f, shadow: 0f, shadowSize: 0f, shadowOffset: 0f);
                _chip.SetShaderPassEnabled("SRPDefaultUnlit", false); // no shadow: its pass would only cost a draw call
                return _chip;
            }
        }

        static Material _card;

        /// <summary>
        /// A card that carries controls (a question band, the pairing pill): a rounded box, no rim, no shadow, drawn
        /// before the controls on it (a quiet button is see-through: drawn first it would show the room, not the card).
        /// </summary>
        public static Material CardMaterial
        {
            get
            {
                if (_card != null) return _card;
                _card = ShapeMaterial("Lotse Card", Ui.QueueCard, RowRadius, rim: 0f, shadow: 0f, shadowSize: 0f, shadowOffset: 0f);
                _card.SetShaderPassEnabled("SRPDefaultUnlit", false);
                return _card;
            }
        }

        /// <summary>A soft round glow, no depth written.</summary>
        public static Material GlowMaterial
        {
            get
            {
                if (_glow != null) return _glow;
                _glow = new Material(Resources.Load<Shader>("LotseGlow")) { name = "Lotse Glow", renderQueue = Ui.QueueGlow };
                return _glow;
            }
        }

        static Material ShapeMaterial(string name, int queue, float radius, float rim, float shadow, float shadowSize, float shadowOffset)
        {
            var m = new Material(Resources.Load<Shader>("LotseShape")) { name = name, renderQueue = queue };
            m.SetFloat("_Radius", radius);
            m.SetFloat("_Rim", rim);
            m.SetFloat("_Gradient", 0f); // no gradients except the orbs
            m.SetFloat("_Shadow", shadow);
            m.SetFloat("_ShadowSize", shadowSize);
            m.SetColor("_RimColor", Line);
            m.SetFloat("_RimWidth", Hair);
            m.SetColor("_ShadowColor", Color.black);
            m.SetFloat("_ShadowOffset", shadowOffset);
            m.SetFloat("_ZWrite", 1f);
            return m;
        }

        static MaterialPropertyBlock _block;
        static readonly int FadeId = Shader.PropertyToID("_Fade");
        static readonly int HighlightId = Shader.PropertyToID("_Highlight");
        static readonly int FaceColorId = Shader.PropertyToID("_FaceColor");
        static readonly int RadiusId = Shader.PropertyToID("_Radius");
        static readonly int RimId = Shader.PropertyToID("_Rim");
        static readonly int RimColorId = Shader.PropertyToID("_RimColor");

        /// <summary>This shape's own border colour (the red border of the card that needs the user).</summary>
        public static void SetRimColor(Renderer r, Color c)
        {
            if (_block == null) _block = new MaterialPropertyBlock();
            r.GetPropertyBlock(_block);
            _block.SetColor(RimColorId, c);
            r.SetPropertyBlock(_block);
        }

        /// <summary>A rounded shape of `size` metres centred on localPos (a Quad primitive without collider).</summary>
        public static MeshRenderer Shape(Transform parent, string name, Vector3 localPos, Vector2 size, Color color, Material style)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Object.Destroy(go.GetComponent<Collider>());
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = style;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            Ui.SetColor(r, color);
            return r;
        }

        /// <summary>A soft round glow of `diameter` centred on localPos.</summary>
        public static MeshRenderer Glow(Transform parent, string name, Vector3 localPos, float diameter, Color color) =>
            Shape(parent, name, localPos, new Vector2(diameter, diameter), color, GlowMaterial);

        /// <summary>Brightens a shape's rim (hover); 0 is plain.</summary>
        public static void SetHighlight(Renderer r, float amount) => SetFloat(r, HighlightId, amount);

        /// <summary>
        /// A text object's fade (TMP's _FaceColor alpha through the property block): no vertex colours change, so the
        /// text mesh is not rebuilt while it fades.
        /// </summary>
        public static void SetTextFade(Renderer r, float fade)
        {
            if (_block == null) _block = new MaterialPropertyBlock();
            r.GetPropertyBlock(_block);
            _block.SetColor(FaceColorId, new Color(1f, 1f, 1f, fade));
            r.SetPropertyBlock(_block);
        }

        /// <summary>This shape's own corner radius in metres (a text field: a rounded box, not a pill).</summary>
        public static void SetRadius(Renderer r, float radius) => SetFloat(r, RadiusId, radius);

        /// <summary>This shape's own hairline edge (0: none, e.g. the selected row).</summary>
        public static void SetRim(Renderer r, float rim) => SetFloat(r, RimId, rim);

        /// <summary>The shape's alpha factor while it fades in (1: fully there).</summary>
        public static void SetFade(Renderer r, float fade) => SetFloat(r, FadeId, fade);

        static void SetFloat(Renderer r, int id, float value)
        {
            if (_block == null) _block = new MaterialPropertyBlock();
            r.GetPropertyBlock(_block);
            _block.SetFloat(id, value);
            r.SetPropertyBlock(_block);
        }
    }
}
