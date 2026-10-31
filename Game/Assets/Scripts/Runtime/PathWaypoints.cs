using UnityEngine;

namespace GameDevAgent.TowerDefense
{
    /// <summary>Path defined by child transforms, in sibling order.</summary>
    public class PathWaypoints : MonoBehaviour
    {
        public int Count => transform.childCount;

        public float TotalLength
        {
            get
            {
                float total = 0f;
                for (int i = 1; i < Count; i++)
                    total += Vector3.Distance(GetPoint(i - 1), GetPoint(i));
                return total;
            }
        }

        /// <summary>World position of the waypoint at <paramref name="index"/>.</summary>
        public Vector3 GetPoint(int index)
        {
            return transform.GetChild(index).position;
        }

        private void OnDrawGizmos()
        {
            Gizmos.color = Color.yellow;
            for (int i = 0; i < Count; i++)
            {
                Gizmos.DrawSphere(GetPoint(i), 0.2f);
                if (i > 0) Gizmos.DrawLine(GetPoint(i - 1), GetPoint(i));
            }
        }
    }
}
