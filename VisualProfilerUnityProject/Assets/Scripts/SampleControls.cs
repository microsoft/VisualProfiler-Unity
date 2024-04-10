// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Microsoft.MixedReality.Profiling.Sample
{
    public class SampleControls : MonoBehaviour
    {
        public GameObject Profiler3D;
        public GameObject Profiler2D;

        public GameObject Ball;
        public Transform BallSpawn;

        private int selectionIndex = 0;
        private string[] selectionStrings = { "Profiler 3D", "Profiler 2D" };

        private void OnGUI()
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

            if (GUI.Button(new Rect(10, 40, 80, 20), "Reset Balls"))
            {
                if (BallSpawn != null)
                {
                    foreach (Transform child in BallSpawn.transform)
                    {
                        Destroy(child.gameObject);
                    }
                }
            }
        }

        private void Start()
        {
            StartCoroutine(SpawnBalls());
        }

        private IEnumerator SpawnBalls()
        {
            while (Ball != null && BallSpawn != null) 
            {
                Instantiate(Ball, BallSpawn).GetComponent<Rigidbody>().angularVelocity = Random.insideUnitSphere;

                yield return new WaitForSeconds(0.1f);
            }
        }
    }
}
