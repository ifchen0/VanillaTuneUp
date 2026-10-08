using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// Right-button drag state for one row of buttons. A plain right click is left untouched;
    /// only once the mouse has moved past a threshold does the drag start and swallow the release.
    /// </summary>
    public class DragController<T> where T : class
    {
        private const float StartDistance = 6f;

        private T pressed;
        private Vector2 pressPos;
        private int releaseSeenFrame = -1;

        public T Dragging { get; private set; }

        /// <summary>
        /// Processes the current event. Returns true when a drag was dropped on a different item.
        /// </summary>
        public bool HandleEvent(Func<Vector2, T> hitTest, out T source, out T target)
        {
            source = null;
            target = null;
            Event e = Event.current;

            if (Dragging != null)
            {
                // The release can be eaten by a window before it reaches us; give up a frame later.
                if (Input.GetMouseButton(1))
                    releaseSeenFrame = -1;
                else if (releaseSeenFrame < 0)
                    releaseSeenFrame = Time.frameCount;
                else if (Time.frameCount > releaseSeenFrame + 1)
                    Cancel();
            }

            switch (e.type)
            {
                case EventType.MouseDown when e.button == 1:
                    pressed = hitTest(e.mousePosition);
                    pressPos = e.mousePosition;
                    break;
                case EventType.MouseDrag when pressed != null:
                    if (Dragging == null && (e.mousePosition - pressPos).sqrMagnitude > StartDistance * StartDistance)
                    {
                        Dragging = pressed;
                        releaseSeenFrame = -1;
                    }
                    if (Dragging != null)
                        e.Use();
                    break;
                case EventType.MouseUp when e.button == 1:
                    if (Dragging != null)
                    {
                        source = Dragging;
                        target = hitTest(e.mousePosition);
                        Cancel();
                        e.Use();
                        GUIUtility.hotControl = 0;
                        return target != null && target != source;
                    }
                    pressed = null;
                    break;
            }
            return false;
        }

        public void Cancel()
        {
            Dragging = null;
            pressed = null;
            releaseSeenFrame = -1;
        }
    }

    public static class DragUtil
    {
        private static readonly Color SourceColor = new Color(1f, 1f, 1f, 0.15f);
        private static readonly Color TargetColor = new Color(1f, 0.85f, 0.3f);

        /// <summary>
        /// Builds the final order: saved keys first, then any key the save does not know about,
        /// placed right after the key that precedes it in the default order.
        /// </summary>
        public static List<string> Merge(IList<string> defaultOrder, List<string> saved)
        {
            var result = new List<string>();
            if (!saved.NullOrEmpty())
            {
                var present = new HashSet<string>(defaultOrder);
                foreach (string key in saved)
                {
                    if (present.Contains(key) && !result.Contains(key))
                        result.Add(key);
                }
            }
            for (int i = 0; i < defaultOrder.Count; i++)
            {
                string key = defaultOrder[i];
                if (result.Contains(key))
                    continue;
                int insertAt = 0;
                for (int j = i - 1; j >= 0; j--)
                {
                    int prev = result.IndexOf(defaultOrder[j]);
                    if (prev >= 0)
                    {
                        insertAt = prev + 1;
                        break;
                    }
                }
                result.Insert(insertAt, key);
            }
            return result;
        }

        /// <summary>Moves the source item into the target's slot.</summary>
        public static void Move<T>(List<T> list, T source, T target)
        {
            int to = list.IndexOf(target);
            if (to < 0 || !list.Remove(source))
                return;
            list.Insert(to, source);
        }

        public static void DrawFeedback(Rect? source, Rect? target, string label)
        {
            if (Event.current.type != EventType.Repaint)
                return;
            if (source.HasValue)
                Widgets.DrawBoxSolid(source.Value, SourceColor);
            if (target.HasValue)
            {
                GUI.color = TargetColor;
                Widgets.DrawBox(target.Value, 2);
                GUI.color = Color.white;
            }
            if (!label.NullOrEmpty())
            {
                Text.Font = GameFont.Small;
                Vector2 size = Text.CalcSize(label);
                Vector2 mouse = Event.current.mousePosition;
                var rect = new Rect(mouse.x + 14f, mouse.y - size.y - 6f, size.x + 12f, size.y + 4f);
                Widgets.DrawWindowBackground(rect);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(rect, label);
                Text.Anchor = TextAnchor.UpperLeft;
            }
        }
    }
}
