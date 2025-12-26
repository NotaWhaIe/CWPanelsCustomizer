using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CWPanelsCustomizer.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CWPanelsCustomizer
{
    public class CurtainGridGenerator : IExternalCommand
    {
        public static string IS_NAME => "Нарезать витраж на кассеты";
        public static string IS_DESCRIPTION => "*Что делает плагин?";

        public static string IS_TAB_NAME => "#BIM";
        public static string IS_IMAGE => "CWPanelsCustomizer.Images.a1.png";

        private SphereByPoint _sphereByPoint;
        private UIDocument _uidoc;
        private Document _doc;

        // Настройки разрезки (мм)
        public double PanelHeight_mm { get; set; } = 1000.0;
        public double PanelWidth_mm { get; set; } = 2000.0;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            _uidoc = commandData.Application.ActiveUIDocument;
            _doc = _uidoc.Document;
            _sphereByPoint = new SphereByPoint(_doc);

            using (TransactionGroup tg = new TransactionGroup(_doc, IS_NAME))
            {
                tg.Start();
                Method();
                tg.Assimilate();
            }

            return Result.Succeeded;
        }

        private void Method()
        {
            IList<ElementId> selectedIds = _uidoc.Selection.GetElementIds().ToList();
            if (selectedIds.Count != 1)
            {
                TaskDialog.Show(IS_NAME, "Выберите один витраж (Curtain Wall).");
                return;
            }

            Wall wall = _doc.GetElement(selectedIds[0]) as Wall;
            if (wall == null)
            {
                TaskDialog.Show(IS_NAME, "Выбранный элемент не является стеной витража (Curtain Wall).");
                return;
            }

            CurtainGrid grid = wall.CurtainGrid;
            if (grid == null)
            {
                TaskDialog.Show(IS_NAME, "Curtain Grid отсутствует. Выбранный элемент не является витражом.");
                return;
            }

            PlanarFace face = GetMainVerticalPlanarFace(wall);
            if (face == null)
            {
                TaskDialog.Show(IS_NAME, "Не удалось получить плоскость витража (PlanarFace) из геометрии.");
                return;
            }

            BoundingBoxXYZ bbox = wall.get_BoundingBox(null);
            if (bbox == null)
            {
                TaskDialog.Show(IS_NAME, "BoundingBox отсутствует.");
                return;
            }

            if (PanelHeight_mm <= 0.0 || PanelWidth_mm <= 0.0)
            {
                TaskDialog.Show(IS_NAME, "Ширина и высота панели должны быть больше 0 мм.");
                return;
            }

            double stepWidth = UnitUtils.ConvertToInternalUnits(PanelHeight_mm, UnitTypeId.Millimeters);
            double stepHeight = UnitUtils.ConvertToInternalUnits(PanelWidth_mm, UnitTypeId.Millimeters);

            XYZ planeOrigin = face.Origin;
            XYZ planeNormal = SafeNormalize(face.FaceNormal);
            if (planeNormal == null)
            {
                TaskDialog.Show(IS_NAME, "Не удалось получить нормаль плоскости витража.");
                return;
            }

            Line uLine = GetFirstGridLine(_doc, grid.GetUGridLineIds());
            Line vLine = GetFirstGridLine(_doc, grid.GetVGridLineIds());

            XYZ dU;
            XYZ dV;

            if (uLine != null && vLine != null)
            {
                dU = SafeNormalize(uLine.Direction);
                dV = SafeNormalize(vLine.Direction);

                if (dU == null || dV == null)
                {
                    TaskDialog.Show(IS_NAME, "Не удалось получить направления U/V линий сетки.");
                    return;
                }

                dU = ProjectDirectionToPlane(dU, planeNormal);
                dV = ProjectDirectionToPlane(dV, planeNormal);

                if (dU == null || dV == null)
                {
                    TaskDialog.Show(IS_NAME, "Направления U/V не удалось спроецировать в плоскость витража.");
                    return;
                }
            }
            else
            {
                LocationCurve lc = wall.Location as LocationCurve;
                Line baseLine = lc != null ? lc.Curve as Line : null;
                if (baseLine == null)
                {
                    TaskDialog.Show(IS_NAME, "Не удалось определить направления: нет U/V линий и нет базовой линии стены.");
                    return;
                }

                XYZ baseDir = SafeNormalize(baseLine.Direction);
                if (baseDir == null)
                {
                    TaskDialog.Show(IS_NAME, "Не удалось определить направление базовой линии.");
                    return;
                }

                dU = ProjectDirectionToPlane(baseDir, planeNormal);
                if (dU == null)
                {
                    TaskDialog.Show(IS_NAME, "Не удалось построить направление в плоскости витража.");
                    return;
                }

                dV = SafeNormalize(planeNormal.CrossProduct(dU));
                if (dV == null)
                {
                    TaskDialog.Show(IS_NAME, "Не удалось построить второе направление в плоскости витража.");
                    return;
                }
            }

            // Направления шага (перпендикуляр к линиям в плоскости)
            XYZ sU = SafeNormalize(planeNormal.CrossProduct(dU)); // шаг для U-линий
            XYZ sV = SafeNormalize(planeNormal.CrossProduct(dV)); // шаг для V-линий
            if (sU == null || sV == null)
            {
                TaskDialog.Show(IS_NAME, "Не удалось построить направления шага в плоскости витража.");
                return;
            }

            XYZ[] corners =
            {
                new XYZ(bbox.Min.X, bbox.Min.Y, bbox.Min.Z),
                new XYZ(bbox.Min.X, bbox.Min.Y, bbox.Max.Z),
                new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Min.Z),
                new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Max.Z),
                new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Min.Z),
                new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Max.Z),
                new XYZ(bbox.Max.X, bbox.Max.Y, bbox.Min.Z),
                new XYZ(bbox.Max.X, bbox.Max.Y, bbox.Max.Z)
            };

            double minSU = double.MaxValue;
            double maxSU = double.MinValue;
            double minSV = double.MaxValue;
            double maxSV = double.MinValue;

            for (int i = 0; i < corners.Length; i++)
            {
                XYZ p = ProjectPointToPlane(corners[i], planeOrigin, planeNormal);
                XYZ v = p - planeOrigin;

                double a = v.DotProduct(sU);
                double b = v.DotProduct(sV);

                if (a < minSU) minSU = a;
                if (a > maxSU) maxSU = a;
                if (b < minSV) minSV = b;
                if (b > maxSV) maxSV = b;
            }

            if (maxSU - minSU < stepWidth * 0.5 || maxSV - minSV < stepHeight * 0.5)
            {
                TaskDialog.Show(IS_NAME, "Габариты витража слишком малы для разрезки заданным шагом.");
                return;
            }

            double midSU = (minSU + maxSU) * 0.5;
            double midSV = (minSV + maxSV) * 0.5;

            // ВАЖНО:
            // - U-линии добавляем с шагом PanelWidth (stepWidth) по оси sU
            // - V-линии добавляем с шагом PanelHeight (stepHeight) по оси sV
            List<double> uOffsets = new List<double>();
            for (double a = minSU + stepWidth; a < maxSU - 1e-6; a += stepWidth)
            {
                uOffsets.Add(a);
            }

            List<double> vOffsets = new List<double>();
            for (double b = minSV + stepHeight; b < maxSV - 1e-6; b += stepHeight)
            {
                vOffsets.Add(b);
            }

            int success = 0;
            int fail = 0;

            using (Transaction t = new Transaction(_doc, "Curtain Grid Divide"))
            {
                t.Start();

                for (int i = 0; i < uOffsets.Count; i++)
                {
                    double a = uOffsets[i];
                    XYZ raw = planeOrigin + sU.Multiply(a) + sV.Multiply(midSV);
                    XYZ pos = ProjectPointToPlane(raw, planeOrigin, planeNormal);

                    if (TryAddGridLine(grid, true, pos))
                    {
                        success++;
                    }
                    else
                    {
                        fail++;
                    }
                }

                for (int i = 0; i < vOffsets.Count; i++)
                {
                    double b = vOffsets[i];
                    XYZ raw = planeOrigin + sU.Multiply(midSU) + sV.Multiply(b);
                    XYZ pos = ProjectPointToPlane(raw, planeOrigin, planeNormal);

                    if (TryAddGridLine(grid, false, pos))
                    {
                        success++;
                    }
                    else
                    {
                        fail++;
                    }
                }

                t.Commit();
            }

            TaskDialog.Show(IS_NAME, $"Готово.\nДобавлено линий: {success}\nОшибок: {fail}");
        }

        private static bool TryAddGridLine(CurtainGrid grid, bool isUGridLine, XYZ position)
        {
            try
            {
                grid.AddGridLine(isUGridLine, position, false);
                return true;
            }
            catch
            {
                try
                {
                    grid.AddGridLine(isUGridLine, position, true);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static XYZ ProjectPointToPlane(XYZ point, XYZ planeOrigin, XYZ planeNormal)
        {
            XYZ v = point - planeOrigin;
            double dist = v.DotProduct(planeNormal);
            return point - planeNormal.Multiply(dist);
        }

        private static XYZ ProjectDirectionToPlane(XYZ direction, XYZ planeNormal)
        {
            XYZ d = direction - planeNormal.Multiply(direction.DotProduct(planeNormal));
            return SafeNormalize(d);
        }

        private static XYZ SafeNormalize(XYZ v)
        {
            if (v == null)
            {
                return null;
            }

            double len = v.GetLength();
            if (len < 1e-9)
            {
                return null;
            }

            return v.Divide(len);
        }

        private static Line GetFirstGridLine(Document doc, ICollection<ElementId> ids)
        {
            if (ids == null || ids.Count == 0)
            {
                return null;
            }

            foreach (ElementId id in ids)
            {
                CurtainGridLine gl = doc.GetElement(id) as CurtainGridLine;
                if (gl == null)
                {
                    continue;
                }

                Line l = gl.FullCurve as Line;
                if (l != null)
                {
                    return l;
                }
            }

            return null;
        }

        private static PlanarFace GetMainVerticalPlanarFace(Wall wall)
        {
            Options options = new Options();
            options.ComputeReferences = false;
            options.IncludeNonVisibleObjects = true;
            options.DetailLevel = ViewDetailLevel.Fine;

            GeometryElement ge = wall.get_Geometry(options);
            if (ge == null)
            {
                return null;
            }

            XYZ wallNormal = wall.Orientation;
            wallNormal = wallNormal != null ? SafeNormalize(wallNormal) : null;
            if (wallNormal == null)
            {
                return null;
            }

            PlanarFace best = null;
            double bestArea = 0.0;

            foreach (GeometryObject go in ge)
            {
                ScanGeometryObject(go, wallNormal, ref best, ref bestArea);
            }

            return best;
        }

        private static void ScanGeometryObject(GeometryObject go, XYZ wallNormal, ref PlanarFace best, ref double bestArea)
        {
            GeometryInstance gi = go as GeometryInstance;
            if (gi != null)
            {
                GeometryElement inst = gi.GetInstanceGeometry();
                if (inst != null)
                {
                    foreach (GeometryObject igo in inst)
                    {
                        ScanGeometryObject(igo, wallNormal, ref best, ref bestArea);
                    }
                }
                return;
            }

            Solid solid = go as Solid;
            if (solid == null || solid.Faces == null || solid.Faces.Size == 0)
            {
                return;
            }

            foreach (Face f in solid.Faces)
            {
                PlanarFace pf = f as PlanarFace;
                if (pf == null)
                {
                    continue;
                }

                XYZ n = SafeNormalize(pf.FaceNormal);
                if (n == null)
                {
                    continue;
                }

                double verticality = Math.Abs(n.DotProduct(XYZ.BasisZ));
                if (verticality > 0.2)
                {
                    continue;
                }

                double align = Math.Abs(n.DotProduct(wallNormal));
                if (align < 0.8)
                {
                    continue;
                }

                double area = pf.Area;
                if (area > bestArea)
                {
                    bestArea = area;
                    best = pf;
                }
            }
        }
    }
}
