using System.Collections.Generic;
using System.Security.Permissions;
using UnityEngine;

namespace LazySpawner
{
    public static class Debug
    {
        public static bool Visible = true;
    }

    public class DrawTransform : MonoBehaviour
    {
        private static HashSet<DrawTransform> instances = new HashSet<DrawTransform>();

        public static bool drawTransforms = true;
        private bool drawEnabled = false;

        LineRenderer xLine;
        LineRenderer yLine;
        LineRenderer zLine;

        internal void Start()
        {
            if (!drawTransforms)
            {
                Destroy(this);
                return;
            }

            xLine = new GameObject().AddComponent<LineRenderer>();
            yLine = new GameObject().AddComponent<LineRenderer>();
            zLine = new GameObject().AddComponent<LineRenderer>();

            SetupLine(xLine, Color.red);
            SetupLine(yLine, Color.green);
            SetupLine(zLine, Color.blue);

            xLine.enabled = drawEnabled;
            yLine.enabled = drawEnabled;
            zLine.enabled = drawEnabled;

            instances.Add(this);
        }

        public static void Cleanup()
        {
            foreach (var instance in instances)
                Destroy(instance);
        }

        protected void OnDestroy()
        {
            instances.Remove(this);
            Destroy(xLine?.gameObject);
            Destroy(yLine?.gameObject);
            Destroy(zLine?.gameObject); 
        }

        // Update is called once per frame
        internal void Update()
        {
            if (Debug.Visible)
            {
                UpdateLine(xLine, transform.right);
                UpdateLine(yLine, transform.up);
                UpdateLine(zLine, transform.forward);
            }

            if (Debug.Visible == drawEnabled)
                return;

            if (Debug.Visible)
            {
                xLine.enabled = true;
                yLine.enabled = true;
                zLine.enabled = true;
            }
            else
            {
                xLine.enabled = false;
                yLine.enabled = false;
                zLine.enabled = false;
            }

            drawEnabled = Debug.Visible;
        }

        void SetupLine(LineRenderer line, Color color)
        {
            line.material = new Material(Shader.Find("Unlit/Color"));
            line.material.color = color;
            line.widthMultiplier = 0.03f;
        }

        void UpdateLine(LineRenderer line, Vector3 direction)
        {
            line.SetPositions(new Vector3[] { transform.position, transform.position + direction * 2 });
        }
    }
}
