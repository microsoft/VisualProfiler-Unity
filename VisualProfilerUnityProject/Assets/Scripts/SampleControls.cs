// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using UnityEngine;

namespace Microsoft.MixedReality.Profiling.Sample
{
    public class SampleControls : MonoBehaviour
    {
        public GameObject Profiler3D;
        public GameObject Profiler2D;

        private int selectionIndex = 0;
        private string[] selectionStrings = { "Profiler 3D", "Profiler 2D" };

        void OnGUI()
        {
            selectionIndex = GUI.SelectionGrid(new Rect(10, 10, 160, 20), selectionIndex, selectionStrings, 2);

            switch (selectionIndex)
            {
                default:
                case 0:
                    {
                        Profiler2D.SetActive(false);
                        Profiler3D.SetActive(true);
                    }
                    break;
                case 1:
                    {
                        Profiler3D.SetActive(false);
                        Profiler2D.SetActive(true);
                    }
                    break;
            }
        }
    }
}
