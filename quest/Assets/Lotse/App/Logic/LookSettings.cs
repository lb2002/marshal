#nullable enable

namespace Lotse.App
{
    /// <summary>
    /// The settings that last across runs: the sound (on by default) and "Ruhig" (off by default: with it nothing in the
    /// workspace moves, Theme.Still). U5a removed the look switch: there is one dark look. Kept in the app's small
    /// key/value store; anything unknown there reads as the default.
    /// </summary>
    public sealed class LookSettings
    {
        public const string SoundKey = "lotse.sound";
        public const string CalmKey = "lotse.calm";
        public const string AutoCollapseKey = "lotse.autocollapse";

        readonly IKeyValueStore _store;

        public LookSettings(IKeyValueStore store)
        {
            _store = store;
        }

        public bool Sound
        {
            get => _store.Get(SoundKey) != "off";
            set => _store.Set(SoundKey, value ? "on" : "off");
        }

        /// <summary>"Ruhig": nothing moves (the orbs stand; their colours, rings and badges keep the states apart).</summary>
        public bool Calm
        {
            get => _store.Get(CalmKey) == "on";
            set => _store.Set(CalmKey, value ? "on" : "off");
        }

        /// <summary>"Automatisch einklappen" (U6, on by default): the window folds into the rail after 20 s with the head away.</summary>
        public bool AutoCollapse
        {
            get => _store.Get(AutoCollapseKey) != "off";
            set => _store.Set(AutoCollapseKey, value ? "on" : "off");
        }
    }
}
