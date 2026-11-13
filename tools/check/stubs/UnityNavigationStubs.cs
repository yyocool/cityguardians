// 형식 검사(tools/check/unity)에만 쓰는 대역. 게임 빌드에는 들어가지 않는다.
// com.unity.ai.navigation 의 NavMeshSurface 가운데 이 프로젝트가 쓰는 멤버만 선언한다.
using UnityEngine;
using UnityEngine.AI;

namespace Unity.AI.Navigation
{
    public enum CollectObjects
    {
        All = 0,
        Volume = 1,
        Children = 2,
        MarkedWithModifier = 3,
    }

    public sealed class NavMeshSurface : MonoBehaviour
    {
        public int agentTypeID { get; set; }
        public CollectObjects collectObjects { get; set; }
        public NavMeshCollectGeometry useGeometry { get; set; }
        public LayerMask layerMask { get; set; }
        public Vector3 size { get; set; }
        public Vector3 center { get; set; }
        public NavMeshData navMeshData { get; set; }

        public void BuildNavMesh() { }
        public void RemoveData() { }
        public AsyncOperation UpdateNavMesh(NavMeshData data) => null;
    }
}
