using Autodesk.Revit.DB;
using System.Collections.Generic;

namespace CWPanelsCustomizer
{
    public class CurtainWallPanelDto
    {
        public ElementId Id { get; set; }
        public FamilyInstance PanelElement { get; set; }
        public BoundingBoxXYZ WorldBoundingBox { get; set; }
        public BoundingBoxXYZ LocalBoundingBox { get; set; }

        public bool IsMirrored { get; set; }
        public PanelSideRelativeToOpening SideRelativeToOpening { get; set; }
            = PanelSideRelativeToOpening.Undefined;
        public double? DxFromOpeningCenterFt { get; set; }
    }

    public class OpeningModelDto
    {
        public ElementId Id { get; set; }
        public FamilyInstance OpeningElement { get; set; }
        public BoundingBoxXYZ WorldBoundingBox { get; set; }
        public BoundingBoxXYZ LocalBoundingBox { get; set; }
    }

    public class CurtainWallDataDto
    {
        public ElementId Id { get; set; }
        public Wall CurtainWallElement { get; set; }
        public Transform InverseTransform { get; set; }
        public List<OpeningModelDto> IntersectingOpenings { get; set; } = new List<OpeningModelDto>();
        public List<CurtainWallPanelDto> Panels { get; set; } = new List<CurtainWallPanelDto>();
    }

    public enum PanelSideRelativeToOpening
    {
        Undefined = 0, // не анализировали или не пересекается
        OnAxis = 1,    // на оси окна (в пределах допуска)
        Left = 2,
        Right = 3
    }
}
