using System.Collections.Generic;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// A glass panel's background stops the hand ray: a button or agent behind a panel cannot be clicked through it,
    /// and a click on the panel itself is no "click elsewhere". Its face is its own Quad (its size is its scale).
    /// </summary>
    public sealed class RayBlocker : MonoBehaviour
    {
        static readonly List<RayBlocker> Enabled = new List<RayBlocker>();

        /// <summary>The panels that stop rays now.</summary>
        public static IReadOnlyList<RayBlocker> All => Enabled;

        /// <summary>The panel's face in world space (its forward points away from the user).</summary>
        public PokeRect Rect => new PokeRect(Ui.ToVec(transform.position), Ui.ToVec(transform.right), Ui.ToVec(transform.up),
            transform.lossyScale.x * 0.5f, transform.lossyScale.y * 0.5f);

        void OnEnable() => Enabled.Add(this);
        void OnDisable() => Enabled.Remove(this);
    }
}
