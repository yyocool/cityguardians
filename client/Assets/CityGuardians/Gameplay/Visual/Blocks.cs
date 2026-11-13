using System.Collections.Generic;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 색마다 재질 하나를 만들어 함께 쓴다. 기준 재질은 Resources/CG 아래에 있고
    /// (에디터 설정 도구가 만든다) 빌드에 셰이더가 포함되게 한다.
    /// </summary>
    public static class MaterialLibrary
    {
        private static Material _litBase;
        private static Material _unlitBase;
        private static readonly Dictionary<int, Material> LitCache = new Dictionary<int, Material>();
        private static readonly Dictionary<int, Material> UnlitCache = new Dictionary<int, Material>();

        public static Material Lit(Color color) => Get(color, LitCache, ref _litBase, "CG/Lit", "Universal Render Pipeline/Lit");

        public static Material Unlit(Color color) => Get(color, UnlitCache, ref _unlitBase, "CG/Unlit", "Universal Render Pipeline/Unlit");

        private static Material Get(Color color, Dictionary<int, Material> cache, ref Material baseMaterial,
            string resourcePath, string shaderName)
        {
            Color32 c = color;
            int key = (c.r << 24) | (c.g << 16) | (c.b << 8) | c.a;
            if (cache.TryGetValue(key, out Material cached) && cached != null) return cached;

            if (baseMaterial == null)
            {
                baseMaterial = Resources.Load<Material>(resourcePath);
                if (baseMaterial == null)
                {
                    Shader shader = Shader.Find(shaderName);
                    if (shader == null)
                    {
                        Debug.LogError($"기준 재질 {resourcePath} 과 셰이더 {shaderName} 을 찾을 수 없습니다. " +
                                       "에디터에서 CityGuardians > Project Setup을 실행하세요.");
                        shader = Shader.Find("Hidden/InternalErrorShader");
                    }
                    baseMaterial = new Material(shader);
                }
            }

            var material = new Material(baseMaterial) { color = color, name = $"cg_{c.r:x2}{c.g:x2}{c.b:x2}" };
            cache[key] = material;
            return material;
        }
    }

    /// <summary>기본 도형으로 블록을 만드는 도우미. 캐릭터, 가구, 건물이 모두 이것으로 조립된다.</summary>
    public static class Blocks
    {
        private static Mesh _cube;
        private static Mesh _cylinder;
        private static Mesh _sphere;
        private static Mesh _quad;

        private static Mesh Cube => _cube != null ? _cube : (_cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx"));
        private static Mesh CylinderMesh => _cylinder != null ? _cylinder : (_cylinder = Resources.GetBuiltinResource<Mesh>("Cylinder.fbx"));
        private static Mesh SphereMesh => _sphere != null ? _sphere : (_sphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx"));

        /// <summary>상자. localPos는 상자 중심.</summary>
        public static GameObject Box(Transform parent, string name, Vector3 localPos, Vector3 size, Color color,
            bool collider = false, bool unlit = false)
        {
            GameObject go = Create(parent, name, localPos, size, Cube, color, unlit);
            if (collider) go.AddComponent<BoxCollider>();
            return go;
        }

        /// <summary>원기둥. 지름 = size.x/size.z, 높이 = size.y.</summary>
        public static GameObject Cylinder(Transform parent, string name, Vector3 localPos, Vector3 size, Color color,
            bool collider = false, bool unlit = false)
        {
            // 기본 원기둥 메시의 높이는 2라서 y 배율을 절반으로 준다.
            var scale = new Vector3(size.x, size.y * 0.5f, size.z);
            GameObject go = Create(parent, name, localPos, scale, CylinderMesh, color, unlit);
            if (collider)
            {
                var capsule = go.AddComponent<CapsuleCollider>();
                capsule.height = 2f;
                capsule.radius = 0.5f;
            }
            return go;
        }

        public static GameObject Sphere(Transform parent, string name, Vector3 localPos, Vector3 size, Color color,
            bool collider = false, bool unlit = false)
        {
            GameObject go = Create(parent, name, localPos, size, SphereMesh, color, unlit);
            if (collider) go.AddComponent<SphereCollider>();
            return go;
        }

        /// <summary>
        /// 한쪽 면만 보이는 판. normal 방향에서 볼 때만 보인다.
        /// 방의 벽에 써서, 방 밖으로 나간 카메라가 벽에 가리지 않고 안을 들여다볼 수 있게 한다.
        /// </summary>
        public static GameObject Panel(Transform parent, string name, Vector3 localPos, Vector3 normal,
            Vector2 size, Color color, bool unlit = false)
        {
            if (_quad == null) _quad = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            GameObject go = Create(parent, name, localPos, new Vector3(size.x, size.y, 1f), _quad, color, unlit);
            // 기본 판은 -Z 쪽에서 보인다.
            go.transform.localRotation = Quaternion.LookRotation(-normal, Vector3.up);
            go.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        /// <summary>보이지 않는 벽.</summary>
        public static GameObject InvisibleWall(Transform parent, string name, Vector3 localPos, Vector3 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.AddComponent<BoxCollider>().size = size;
            return go;
        }

        /// <summary>회전의 중심이 되는 빈 오브젝트.</summary>
        public static Transform Pivot(Transform parent, string name, Vector3 localPos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            return go.transform;
        }

        public static void SetColor(GameObject block, Color color, bool unlit = false)
        {
            var renderer = block.GetComponent<MeshRenderer>();
            if (renderer != null)
                renderer.sharedMaterial = unlit ? MaterialLibrary.Unlit(color) : MaterialLibrary.Lit(color);
        }

        private static GameObject Create(Transform parent, string name, Vector3 localPos, Vector3 scale, Mesh mesh,
            Color color, bool unlit)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = unlit ? MaterialLibrary.Unlit(color) : MaterialLibrary.Lit(color);
            if (unlit) renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }
    }
}
