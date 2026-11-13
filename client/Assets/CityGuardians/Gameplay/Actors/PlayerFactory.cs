using CityGuardians.Services;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    /// <summary>플레이어와 카메라를 조립한다. 방과 도시가 같은 것을 쓴다.</summary>
    public static class PlayerFactory
    {
        public static PlayerController Spawn(Vector3 position, float yaw, Color skyColor, float cameraDistance)
        {
            GameSession session = GameSession.Current;

            var go = new GameObject("Player");
            go.layer = 2; // Ignore Raycast: 카메라 충돌 검사에서 빠진다
            go.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));

            var body = go.AddComponent<CharacterController>();
            body.height = BlockCharacter.Height;
            body.radius = 0.3f;
            body.center = new Vector3(0f, BlockCharacter.Height * 0.5f, 0f);
            body.stepOffset = 0.3f;
            body.slopeLimit = 45f;
            body.skinWidth = 0.03f;

            BlockCharacter character = BlockCharacter.Create(go.transform, Appearance.FromSeed(session.Me.playerId));
            character.gameObject.AddComponent<BlockCharacterAnimator>();
            character.gameObject.AddComponent<HitFlash>();

            var health = go.AddComponent<Health>();
            health.team = Team.Player;
            health.invulnerableAfterHit = 0.3f;
            health.Init(session.Me.hpMax);

            var controller = go.AddComponent<PlayerController>();
            controller.Setup(character, health);
            controller.respawnPoint = position;
            controller.respawnYaw = yaw;
            ApplyArmband(controller);
            go.AddComponent<PlayerCombat>();
            go.AddComponent<PlayerInteractor>();
            // 입은 방어구와 옷을 모습과 방어에 맞춘다.
            go.AddComponent<PlayerWear>();

            var cameraObject = new GameObject("MainCamera") { tag = "MainCamera" };
            var cam = cameraObject.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = skyColor;
            cam.fieldOfView = 55f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 400f;
            cameraObject.AddComponent<AudioListener>();
            var orbit = cameraObject.AddComponent<OrbitCamera>();
            orbit.target = go.transform;
            orbit.distance = cameraDistance;
            orbit.SnapBehind(yaw);

            return controller;
        }

        /// <summary>
        /// 완장을 소속에 맞춘다: 주민은 자기 도시의 색, 이방인은 회색 (docs/RESIDENCY_SYSTEM.md 7장).
        /// 전입신고를 마친 직후에도 부른다.
        /// </summary>
        public static void ApplyArmband(PlayerController player)
        {
            GameSession session = GameSession.Current;
            if (player == null || session == null) return;
            // 깜빡이는 중이었다면 먼저 원래 재질로 돌려놓는다. 그러지 않으면 새 색이 덮여 사라진다.
            var flash = player.Character.GetComponent<HitFlash>();
            if (flash != null) flash.Capture();
            player.Character.SetArmbandColor(session.IsResident
                ? RegionColors.OfRegion(session.Me.regionCode)
                : Appearance.StrangerArmband);
        }

        /// <summary>무기를 손에 든다. 이때부터 공격할 수 있다. 처음에는 배턴이고, 그 뒤로는 마지막에 들고 있던 무기다.</summary>
        public static void Arm(PlayerController player)
        {
            var combat = player.GetComponent<PlayerCombat>();
            if (combat.armed) return;
            combat.armed = true;
            combat.ShowWeapon(GameSession.Current.ActiveWeapon);
        }
    }
}
