using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace CWPanelsCustomizer.Helpers
{
    internal class SphereByPoint
    {
        private readonly Document _doc;

        public SphereByPoint(Document doc)
        {
            _doc = doc;
        }

        public void CreateSphereByPoint(XYZ center, string comment)
        {
            List<Curve> profile = new();
            double radius = 0.2;
            XYZ profilePlus = center + new XYZ(0, radius, 0);
            XYZ profileMinus = center - new XYZ(0, radius, 0);

            profile.Add(Line.CreateBound(profilePlus, profileMinus));
            profile.Add(Arc.Create(profileMinus, profilePlus, center + new XYZ(radius, 0, 0)));

            CurveLoop curveLoop = CurveLoop.Create(profile);
            SolidOptions options = new SolidOptions(ElementId.InvalidElementId, ElementId.InvalidElementId);

            Frame frame = new Frame(center, XYZ.BasisX, -XYZ.BasisZ, XYZ.BasisY);
            if (Frame.CanDefineRevitGeometry(frame))
            {

                Solid sphere = GeometryCreationUtilities.CreateRevolvedGeometry(frame, new CurveLoop[] { curveLoop }, 0, 2 * Math.PI, options);

                using (Transaction t = new(_doc, "create SphereByPoint"))
                {
                    t.Start();
                    DirectShape ds = DirectShape.CreateElement(_doc, new ElementId(BuiltInCategory.OST_Furniture));
                    ds.ApplicationId = "Application id";
                    ds.ApplicationDataId = "Geometry object id";
                    ds.SetShape(new GeometryObject[] { sphere });
                    ds.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Set(comment);
                    t.Commit();
                }
            }
        }

        public void CreateSphereByPoint(XYZ center)
        {
            CreateSphereByPoint(center, string.Empty);
        }
    }
}
