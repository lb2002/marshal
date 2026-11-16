#nullable enable
using System;

namespace Lotse.App
{
    /// <summary>
    /// What we keep about the desk anchor: its uuid under one key, nothing else. Decides what a save, a failed
    /// save and a missing anchor mean for that key and which anchors to erase from the device; the anchor itself
    /// lives on the device. Re-places can overlap (a save takes seconds): each save is an attempt, and only the
    /// newest attempt may change the key. Anything an older attempt produced is erased instead.
    /// </summary>
    public sealed class DeskAnchorRecord
    {
        public const string Key = "lotse.desk.anchor";

        /// <summary>One save in flight: which attempt it is and what was stored when it began.</summary>
        public sealed class SaveAttempt
        {
            internal readonly int Number;
            internal readonly Guid? Replacing;

            internal SaveAttempt(int number, Guid? replacing)
            {
                Number = number;
                Replacing = replacing;
            }
        }

        readonly IKeyValueStore _store;
        int _attempts;

        readonly string _key;

        public DeskAnchorRecord(IKeyValueStore store) : this(store, Key) { }

        /// <summary>The same record under another key (U5b: the display's anchor).</summary>
        public DeskAnchorRecord(IKeyValueStore store, string key)
        {
            _store = store;
            _key = key;
        }

        public bool HasSaved => TryGetSaved(out _);

        public bool TryGetSaved(out Guid uuid)
        {
            uuid = Guid.Empty;
            var s = _store.Get(_key);
            return s != null && Guid.TryParse(s, out uuid) && uuid != Guid.Empty;
        }

        /// <summary>A new anchor is being saved; call when it was created.</summary>
        public SaveAttempt BeginSave() => new SaveAttempt(++_attempts, TryGetSaved(out var old) ? old : (Guid?)null);

        /// <summary>
        /// The attempt's anchor was saved on the device. The newest attempt stores its uuid and returns the
        /// previous one to erase (if different); an older one stores nothing and returns its own uuid to erase.
        /// </summary>
        public Guid? Saved(SaveAttempt attempt, Guid uuid)
        {
            if (attempt.Number != _attempts) return uuid;
            var hadOld = TryGetSaved(out var old);
            _store.Set(_key, uuid.ToString());
            return hadOld && old != uuid ? old : (Guid?)null;
        }

        /// <summary>
        /// The attempt's anchor could not be saved, so the stored one marks the old spot: forget it and return it
        /// to erase. Only the newest attempt does this, and only while the key still holds what it replaced.
        /// </summary>
        public Guid? SaveFailed(SaveAttempt attempt)
        {
            if (attempt.Number != _attempts || attempt.Replacing == null) return null;
            if (!TryGetSaved(out var stored) || stored != attempt.Replacing.Value) return null;
            _store.Delete(_key);
            return stored;
        }

        /// <summary>The device no longer has the stored anchor: do not look for it again.</summary>
        public void NotFound() => _store.Delete(_key);
    }
}
