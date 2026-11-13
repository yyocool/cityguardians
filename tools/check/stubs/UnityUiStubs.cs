// 형식 검사(tools/check/unity)에만 쓰는 대역. 게임 빌드에는 들어가지 않는다.
// com.unity.ugui (UnityEngine.UI) 가운데 이 프로젝트가 쓰는 멤버만 선언한다. 동작은 없다.
using System;
using System.Collections.Generic;

namespace UnityEngine.EventSystems
{
    public abstract class UIBehaviour : MonoBehaviour
    {
        protected virtual void Awake() { }
        protected virtual void OnEnable() { }
        protected virtual void Start() { }
        protected virtual void OnDisable() { }
        protected virtual void OnDestroy() { }
        protected virtual void OnRectTransformDimensionsChange() { }
        public virtual bool IsActive() => isActiveAndEnabled;
    }
}

namespace UnityEngine.UI
{
    public class VertexHelper : IDisposable
    {
        public int currentVertCount => 0;
        public int currentIndexCount => 0;
        public void Clear() { }
        public void AddVert(Vector3 position, Color32 color, Vector4 uv0) { }
        public void AddVert(UIVertex v) { }
        public void AddTriangle(int idx0, int idx1, int idx2) { }
        public void AddUIVertexQuad(UIVertex[] verts) { }
        public void AddUIVertexStream(List<UIVertex> verts, List<int> indices) { }
        public void FillMesh(Mesh mesh) { }
        public void Dispose() { }
    }

    public abstract class Graphic : UnityEngine.EventSystems.UIBehaviour
    {
        public virtual Color color { get; set; }
        public virtual bool raycastTarget { get; set; }
        public virtual Material material { get; set; }
        public virtual Texture mainTexture => null;
        public RectTransform rectTransform => (RectTransform)transform;
        public Canvas canvas => null;
        public CanvasRenderer canvasRenderer => null;
        public virtual void SetAllDirty() { }
        public virtual void SetVerticesDirty() { }
        public virtual void SetMaterialDirty() { }
        public virtual void SetLayoutDirty() { }
        protected virtual void OnPopulateMesh(VertexHelper vh) { }
        public virtual void CrossFadeAlpha(float alpha, float duration, bool ignoreTimeScale) { }
        public virtual void CrossFadeColor(Color targetColor, float duration, bool ignoreTimeScale, bool useAlpha) { }
        public Rect GetPixelAdjustedRect() => default;
    }

    public abstract class MaskableGraphic : Graphic
    {
        public bool maskable { get; set; }
    }

    public class Image : MaskableGraphic
    {
        public enum Type { Simple, Sliced, Tiled, Filled }
        public enum FillMethod { Horizontal, Vertical, Radial90, Radial180, Radial360 }

        public Sprite sprite { get; set; }
        public Type type { get; set; }
        public FillMethod fillMethod { get; set; }
        public float fillAmount { get; set; }
        public int fillOrigin { get; set; }
        public bool preserveAspect { get; set; }
        public float pixelsPerUnitMultiplier { get; set; }
    }

    public class RawImage : MaskableGraphic
    {
        public Texture texture { get; set; }
        public Rect uvRect { get; set; }
    }

    public class Text : MaskableGraphic
    {
        public virtual string text { get; set; }
        public Font font { get; set; }
        public int fontSize { get; set; }
        public FontStyle fontStyle { get; set; }
        public TextAnchor alignment { get; set; }
        public bool supportRichText { get; set; }
        public bool resizeTextForBestFit { get; set; }
        public int resizeTextMinSize { get; set; }
        public int resizeTextMaxSize { get; set; }
        public HorizontalWrapMode horizontalOverflow { get; set; }
        public VerticalWrapMode verticalOverflow { get; set; }
        public float lineSpacing { get; set; }
        public bool alignByGeometry { get; set; }
        public virtual float preferredWidth => 0f;
        public virtual float preferredHeight => 0f;
        public TextGenerator cachedTextGenerator => null;
        public TextGenerator cachedTextGeneratorForLayout => null;
        public TextGenerationSettings GetGenerationSettings(Vector2 extents) => default;
    }

    public abstract class BaseMeshEffect : UnityEngine.EventSystems.UIBehaviour
    {
        public abstract void ModifyMesh(VertexHelper vh);
    }

    public class Shadow : BaseMeshEffect
    {
        public Color effectColor { get; set; }
        public Vector2 effectDistance { get; set; }
        public bool useGraphicAlpha { get; set; }
        public override void ModifyMesh(VertexHelper vh) { }
    }

    public class Outline : Shadow { }

    public class CanvasScaler : UnityEngine.EventSystems.UIBehaviour
    {
        public enum ScaleMode { ConstantPixelSize, ScaleWithScreenSize, ConstantPhysicalSize }
        public enum ScreenMatchMode { MatchWidthOrHeight, Expand, Shrink }

        public ScaleMode uiScaleMode { get; set; }
        public Vector2 referenceResolution { get; set; }
        public ScreenMatchMode screenMatchMode { get; set; }
        public float matchWidthOrHeight { get; set; }
        public float scaleFactor { get; set; }
        public float referencePixelsPerUnit { get; set; }
        public float dynamicPixelsPerUnit { get; set; }
    }

    public class RectMask2D : UnityEngine.EventSystems.UIBehaviour
    {
        public Vector4 padding { get; set; }
    }

    public class GraphicRaycaster : UnityEngine.EventSystems.UIBehaviour
    {
        public bool ignoreReversedGraphics { get; set; }
    }
}
