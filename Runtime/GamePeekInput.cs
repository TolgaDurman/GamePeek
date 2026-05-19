using System;
using UnityEngine;

namespace GamePeek
{
    /// <summary>
    /// Runtime bridge for receiving touch events from the GamePeek companion app.
    /// Subscribe to <see cref="OnTouch"/> from any MonoBehaviour to react to phone touches.
    /// </summary>
    public static class GamePeekInput
    {
        /// <summary>
        /// Fired on the main thread whenever a touch event arrives from the phone.
        /// Argument is the normalised position: x=0 left, x=1 right, y=0 top, y=1 bottom.
        /// </summary>
        public static Action<Vector2> OnTouch;

        /// <summary>
        /// Fired on the main thread for every touch event with full details.
        /// Args: fingerId, phase ("began"|"moved"|"ended"|"canceled"), normalised position.
        /// </summary>
        public static Action<int, string, Vector2> OnTouchDetailed;
    }
}
