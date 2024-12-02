using Expansions.Missions.Editor;
using System;
using UnityEngine;

namespace LazySpawner
{
    public interface ITextField
    {
        string Text { get; set; }
        bool Valid { get; }
        void Draw(ref bool ready);
    }

    public class TextField<T> : ITextField
    {
        public string title;
        public T value;

        public Func<string, T> parser;

        public string Text { get => text; set => text = value; }
        public string text;
        private string last;

        public bool Valid { get => _valid; private set => _valid = value; }
        private bool _valid = true;

        public TextField(string title, string text, Func<string, T> parser)
        {
            this.title = title;
            this.text = text;
            this.parser = parser;

            TryParse(out value);
        }

        public bool TryParse(out T result)
        {
            try
            {
                result = parser(text);
                last = text;
                return true;
            }
            catch
            {
                result = default;
                last = text;
                return false;
            }
        }

        public void Draw(ref bool ready)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(title + ": ", GUILayout.Width(IMGUI.windowWidth * IMGUI.fieldNameProportion));

            if (!Valid)
                GUI.color = Color.red;

            text = GUILayout.TextField(text);

            if (!Valid)
                GUI.color = Color.white;

            if (text != last)
                Valid = TryParse(out value) && value != null;

            ready = ready && Valid;

            GUILayout.EndHorizontal();
        }
    }
}
