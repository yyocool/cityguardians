using UnityEngine;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// Boot 장면에 놓이는 시작점.
    /// 장면 파일이 이 스크립트를 가리키므로 파일 이름과 클래스 이름이 같아야 한다
    /// (다른 파일 안에 두면 장면을 다시 만들 때 참조가 끊어진다).
    /// </summary>
    public sealed class BootScene : MonoBehaviour
    {
        private void Start()
        {
            GameRoot.Instance.BeginBoot();
        }
    }
}
