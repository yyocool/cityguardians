using System.IO;
using CityGuardians.Gameplay;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace CityGuardians.EditorTools
{
    /// <summary>
    /// 프로젝트에 필요한 에셋을 만든다: 기준 재질 2개와 장면 5개, 빌드 장면 목록.
    /// 장면의 내용물은 실행 중에 코드가 조립하므로 장면 파일에는 시작점 하나만 들어 있다.
    /// 여러 번 실행해도 같은 결과가 나온다.
    ///
    /// 명령줄: -executeMethod CityGuardians.EditorTools.ProjectSetup.Run
    /// </summary>
    public static class ProjectSetup
    {
        private const string Root = "Assets/CityGuardians";
        private const string MaterialDir = Root + "/Resources/CG";
        private const string SceneDir = Root + "/Scenes";

        public static readonly string[] ScenePaths =
        {
            SceneDir + "/" + SceneNames.Boot + ".unity",
            SceneDir + "/" + SceneNames.Room + ".unity",
            SceneDir + "/" + SceneNames.City + ".unity",
            SceneDir + "/" + SceneNames.Defense + ".unity",
            SceneDir + "/" + SceneNames.Fps + ".unity",
            SceneDir + "/" + SceneNames.Coop + ".unity",
        };

        [MenuItem("CityGuardians/Project Setup")]
        public static void Run()
        {
            Directory.CreateDirectory(MaterialDir);
            Directory.CreateDirectory(SceneDir);
            AssetDatabase.Refresh();

            CreateMaterial("Lit", "Universal Render Pipeline/Lit", material =>
            {
                material.SetFloat("_Smoothness", 0.12f);
                material.SetFloat("_Metallic", 0f);
            });
            CreateMaterial("Unlit", "Universal Render Pipeline/Unlit", null);

            CreateScene(ScenePaths[0], typeof(BootScene), false);
            CreateScene(ScenePaths[1], typeof(RoomBuilder), false);
            CreateScene(ScenePaths[2], typeof(CityBuilder), true);
            CreateScene(ScenePaths[3], typeof(DefenseBuilder), true);
            CreateScene(ScenePaths[4], typeof(FpsBuilder), true);
            CreateScene(ScenePaths[5], typeof(CoopBuilder), true);

            var scenes = new EditorBuildSettingsScene[ScenePaths.Length];
            for (int i = 0; i < ScenePaths.Length; i++) scenes[i] = new EditorBuildSettingsScene(ScenePaths[i], true);
            EditorBuildSettings.scenes = scenes;

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("CityGuardians ProjectSetup 완료: 재질 2개, 장면 " + ScenePaths.Length + "개");
        }

        private static void CreateMaterial(string name, string shaderName, System.Action<Material> configure)
        {
            string path = MaterialDir + "/" + name + ".mat";
            Shader shader = Shader.Find(shaderName);
            if (shader == null) throw new System.InvalidOperationException("셰이더를 찾을 수 없습니다: " + shaderName);

            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                configure?.Invoke(material);
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = shader;
                configure?.Invoke(material);
                EditorUtility.SetDirty(material);
            }
        }

        private static void CreateScene(string path, System.Type entryPoint, bool outdoor)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var entry = (MonoBehaviour)new GameObject(entryPoint.Name, entryPoint).GetComponent(entryPoint);

            // 시작점 스크립트가 에셋으로 잡히지 않으면 장면에 끊어진 참조가 저장되고, 실행했을 때 아무 일도 일어나지 않는다.
            // (클래스가 자기 이름과 다른 파일 안에 있을 때 생긴다.)
            string scriptPath = AssetDatabase.GetAssetPath(MonoScript.FromMonoBehaviour(entry));
            if (string.IsNullOrEmpty(scriptPath))
                throw new System.InvalidOperationException(
                    $"{entryPoint.Name} 스크립트 에셋을 찾을 수 없습니다. {entryPoint.Name}.cs 파일에 따로 두세요.");

            // 실행 중에 다시 설정하지만, 장면에도 같은 값을 넣어 둔다.
            // 안개를 켠 장면이 하나라도 있어야 빌드에 안개 셰이더가 포함된다.
            RenderSettings.skybox = null;
            if (outdoor)
            {
                RenderSettings.ambientMode = AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = new Color(0.68f, 0.80f, 0.94f);
                RenderSettings.ambientEquatorColor = new Color(0.62f, 0.64f, 0.66f);
                RenderSettings.ambientGroundColor = new Color(0.40f, 0.38f, 0.36f);
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.Linear;
                RenderSettings.fogColor = CityBuilder.Sky;
                RenderSettings.fogStartDistance = 70f;
                RenderSettings.fogEndDistance = 300f;
            }
            else
            {
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.62f, 0.62f, 0.68f);
                RenderSettings.fog = false;
            }

            if (!EditorSceneManager.SaveScene(scene, path))
                throw new System.InvalidOperationException("장면을 저장하지 못했습니다: " + path);
        }
    }

    /// <summary>
    /// Windows 개발 빌드를 만든다. 결과물은 저장소 밖으로 나가지 않는 /builds 폴더에 놓인다 (.gitignore 대상).
    /// 개발 빌드에는 로컬 모의 서버가 들어 있으므로 배포용이 아니다.
    ///
    /// 명령줄: -executeMethod CityGuardians.EditorTools.BuildScript.BuildWindowsDev
    /// </summary>
    public static class BuildScript
    {
        [MenuItem("CityGuardians/Build Windows (Development)")]
        public static void BuildWindowsDev()
        {
            string output = Path.GetFullPath(Path.Combine(Application.dataPath, "../../builds/windows/CityGuardians.exe"));
            Directory.CreateDirectory(Path.GetDirectoryName(output));

            var options = new BuildPlayerOptions
            {
                scenes = ProjectSetup.ScenePaths,
                locationPathName = output,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development,
            };

            UnityEditor.Build.Reporting.BuildReport report = BuildPipeline.BuildPlayer(options);
            UnityEditor.Build.Reporting.BuildSummary summary = report.summary;
            Debug.Log($"빌드 결과: {summary.result}, 오류 {summary.totalErrors}개, 크기 {summary.totalSize / (1024 * 1024)} MB, 경로 {output}");

            if (summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                if (Application.isBatchMode) EditorApplication.Exit(1);
                else throw new System.InvalidOperationException("빌드 실패: " + summary.result);
            }
        }
    }
}
