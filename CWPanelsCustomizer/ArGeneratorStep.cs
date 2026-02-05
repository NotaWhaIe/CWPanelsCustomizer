using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CWPanelsCustomizer.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CWPanelsCustomizer
{
    public class ArGeneratorStep : IExternalCommand
    {
        public static string IS_TAB_NAME => "АР";
        public static string IS_NAME => "Нарезать витраж со сдвижкой";
        public static string IS_DESCRIPTION => "Нарезка витражной сеткой 2000x2000 с кирпичным смещением (каждый нечётный ряд +50% ширины)";
        public static string IS_IMAGE => "CWPanelsCustomizer.Images.a1.png";

        private SphereByPoint _sphereByPoint;
        private UIDocument _uidoc;
        private Document _doc;

        // Настройки (мм)
        public double PanelWidthMillimeters { get; set; } = 2000.0;
        public double PanelHeightMillimeters { get; set; } = 1000.0;

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

            if (PanelWidthMillimeters <= 0.0 || PanelHeightMillimeters <= 0.0)
            {
                TaskDialog.Show(IS_NAME, "Ширина и высота панели должны быть больше 0 мм.");
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

            double stepWidth = UnitUtils.ConvertToInternalUnits(PanelWidthMillimeters, UnitTypeId.Millimeters);
            double stepHeight = UnitUtils.ConvertToInternalUnits(PanelHeightMillimeters, UnitTypeId.Millimeters);
            double halfWidth = stepWidth * 0.5;

            XYZ planeOrigin = face.Origin;
            XYZ planeNormal = SafeNormalize(face.FaceNormal);
            if (planeNormal == null)
            {
                TaskDialog.Show(IS_NAME, "Не удалось получить нормаль плоскости витража.");
                return;
            }

            // Направления по реальной U/V сетке, если есть; иначе fallback от оси стены
            Line uLine = GetFirstGridLine(_doc, grid.GetUGridLineIds());
            Line vLine = GetFirstGridLine(_doc, grid.GetVGridLineIds());

            XYZ dU;
            XYZ dV;

            if (uLine != null && vLine != null)
            {
                dU = ProjectDirectionToPlane(SafeNormalize(uLine.Direction), planeNormal);
                dV = ProjectDirectionToPlane(SafeNormalize(vLine.Direction), planeNormal);
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

                dU = ProjectDirectionToPlane(SafeNormalize(baseLine.Direction), planeNormal);
                dV = dU != null ? SafeNormalize(planeNormal.CrossProduct(dU)) : null;
            }

            if (dU == null || dV == null)
            {
                TaskDialog.Show(IS_NAME, "Не удалось получить направления U/V в плоскости витража.");
                return;
            }

            // Направления шага: перпендикуляр к направлениям линий
            // sU — ось “ширины” (по ней стоят вертикальные швы/U-линии)
            // sV — ось “высоты” (по ней идут ряды)
            XYZ sU = SafeNormalize(planeNormal.CrossProduct(dU));
            XYZ sV = SafeNormalize(planeNormal.CrossProduct(dV));
            if (sU == null || sV == null)
            {
                TaskDialog.Show(IS_NAME, "Не удалось построить направления шага в плоскости витража.");
                return;
            }

            // Диапазоны в координатах шага
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

            // 1) Горизонтальные линии (V-линии) — без смещения, шаг по высоте
            List<double> vOffsets = new List<double>();
            for (double b = minSV + stepHeight; b < maxSV - 1e-6; b += stepHeight)
            {
                vOffsets.Add(b);
            }

            // 2) Вертикальные швы (U-линии) — два набора:
            // A: 0, 2000, 4000...
            // B: 1000, 3000, 5000... (сдвиг на 50%)
            List<double> uOffsetsA = new List<double>();
            for (double a = minSU + stepWidth; a < maxSU - 1e-6; a += stepWidth)
            {
                uOffsetsA.Add(a);
            }

            List<double> uOffsetsB = new List<double>();
            for (double a = minSU + halfWidth; a < maxSU - 1e-6; a += stepWidth)
            {
                if (a > minSU + 1e-6 && a < maxSU - 1e-6)
                {
                    uOffsetsB.Add(a);
                }
            }

            int addSuccess = 0;
            int addFail = 0;

            using (Transaction t = new Transaction(_doc, "Curtain Grid Brick Pattern"))
            {
                t.Start();

                // Сначала горизонтали (ряды)
                for (int i = 0; i < vOffsets.Count; i++)
                {
                    double b = vOffsets[i];
                    XYZ raw = planeOrigin + sU.Multiply(midSU) + sV.Multiply(b);
                    XYZ pos = ProjectPointToPlane(raw, planeOrigin, planeNormal);

                    if (TryAddGridLine(grid, false, pos))
                    {
                        addSuccess++;
                    }
                    else
                    {
                        addFail++;
                    }
                }

                // Потом вертикали A
                for (int i = 0; i < uOffsetsA.Count; i++)
                {
                    double a = uOffsetsA[i];
                    XYZ raw = planeOrigin + sU.Multiply(a) + sV.Multiply(midSV);
                    XYZ pos = ProjectPointToPlane(raw, planeOrigin, planeNormal);

                    if (TryAddGridLine(grid, true, pos))
                    {
                        addSuccess++;
                    }
                    else
                    {
                        addFail++;
                    }
                }

                // Потом вертикали B (смещённые)
                for (int i = 0; i < uOffsetsB.Count; i++)
                {
                    double a = uOffsetsB[i];
                    XYZ raw = planeOrigin + sU.Multiply(a) + sV.Multiply(midSV);
                    XYZ pos = ProjectPointToPlane(raw, planeOrigin, planeNormal);

                    if (TryAddGridLine(grid, true, pos))
                    {
                        addSuccess++;
                    }
                    else
                    {
                        addFail++;
                    }
                }

                _doc.Regenerate();

                // Теперь: удалить сегменты “лишних” вертикалей по рядам (brick)
                int removeSuccess = 0;
                int removeFail = 0;

                ICollection<ElementId> uIds = grid.GetUGridLineIds();
                List<CurtainGridLine> uLinesAll = uIds
                    .Select(id => _doc.GetElement(id) as CurtainGridLine)
                    .Where(x => x != null)
                    .ToList();

                for (int i = 0; i < uLinesAll.Count; i++)
                {
                    CurtainGridLine gl = uLinesAll[i];
                    if (!TryClassifyUGridLine(gl, planeOrigin, planeNormal, sU, minSU, stepWidth, out bool isGroupA, out bool isGroupB))
                    {
                        continue;
                    }

                    // Сегменты, которые реально существуют
                    CurveArray segs = gl.ExistingSegmentCurves;
                    if (segs == null || segs.Size == 0)
                    {
                        continue;
                    }

                    // Берём “срез” сегментов в список (для итерации), но удаляем осторожно:
                    List<Curve> segList = new List<Curve>();
                    foreach (Curve c in segs)
                    {
                        if (c != null)
                        {
                            segList.Add(c);
                        }
                    }

                    if (segList.Count == 0)
                    {
                        continue;
                    }

                    for (int s = 0; s < segList.Count; s++)
                    {
                        // Перед каждым удалением обновляем актуальное состояние, чтобы не пытаться удалить “последний” сегмент
                        CurveArray currentSegs = gl.ExistingSegmentCurves;
                        if (currentSegs == null || currentSegs.Size <= 1)
                        {
                            break;
                        }

                        Curve seg = segList[s];
                        if (seg == null)
                        {
                            continue;
                        }

                        XYZ mp = GetCurveMidPoint(seg);
                        mp = ProjectPointToPlane(mp, planeOrigin, planeNormal);

                        double bMid = (mp - planeOrigin).DotProduct(sV);
                        int rowIndex = (int)Math.Floor((bMid - minSV) / stepHeight + 1e-9);

                        if (rowIndex < 0)
                        {
                            continue;
                        }

                        // Чётный ряд: оставляем A; Нечётный ряд: оставляем B
                        bool keepGroupAInThisRow = (rowIndex % 2 == 0);

                        bool shouldRemove =
                            (keepGroupAInThisRow && isGroupB) ||
                            (!keepGroupAInThisRow && isGroupA);

                        if (!shouldRemove)
                        {
                            continue;
                        }

                        try
                        {
                            // Важно: seg должен реально существовать сейчас. Если Revit поменял объект кривой,
                            // пробуем найти ближайший сегмент по midpoint.
                            Curve segToRemove = FindBestMatchingSegmentByMidpoint(gl, seg, planeOrigin, planeNormal);
                            if (segToRemove == null)
                            {
                                removeFail++;
                                continue;
                            }

                            gl.RemoveSegment(segToRemove);
                            removeSuccess++;
                        }
                        catch
                        {
                            removeFail++;
                        }
                    }
                }

                t.Commit();

                TaskDialog.Show(
                    IS_NAME,
                    "Готово.\n" +
                    $"Добавление линий: success={addSuccess}, fail={addFail}\n" +
                    $"Удаление сегментов: success={removeSuccess}, fail={removeFail}\n\n" +
                    "Результат: каждый нечётный ряд смещён вправо на 50% ширины."
                );
            }
        }

        private static Curve FindBestMatchingSegmentByMidpoint(CurtainGridLine gl, Curve original, XYZ planeOrigin, XYZ planeNormal)
        {
            if (gl == null || original == null)
            {
                return null;
            }

            CurveArray currentSegs = gl.ExistingSegmentCurves;
            if (currentSegs == null || currentSegs.Size == 0)
            {
                return null;
            }

            XYZ targetMp = GetCurveMidPoint(original);
            targetMp = ProjectPointToPlane(targetMp, planeOrigin, planeNormal);

            Curve best = null;
            double bestDist = double.MaxValue;

            foreach (Curve c in currentSegs)
            {
                if (c == null)
                {
                    continue;
                }

                XYZ mp = GetCurveMidPoint(c);
                mp = ProjectPointToPlane(mp, planeOrigin, planeNormal);

                double d = mp.DistanceTo(targetMp);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = c;
                }
            }

            // “Достаточно близко” — иначе возвращаем как есть (пусть попробует RemoveSegment и упадёт в catch)
            return best;
        }

        private static bool TryClassifyUGridLine(
            CurtainGridLine gridLine,
            XYZ planeOrigin,
            XYZ planeNormal,
            XYZ sU,
            double minSU,
            double stepWidth,
            out bool isGroupA,
            out bool isGroupB)
        {
            isGroupA = false;
            isGroupB = false;

            Curve full = gridLine.FullCurve;
            if (full == null)
            {
                return false;
            }

            XYZ mp = GetCurveMidPoint(full);
            mp = ProjectPointToPlane(mp, planeOrigin, planeNormal);

            double a = (mp - planeOrigin).DotProduct(sU);
            double rem = PositiveMod(a - minSU, stepWidth);

            // Классификация по остатку: около 0 => A, около 0.5*step => B
            double tol = stepWidth * 0.25;

            if (rem < tol || rem > stepWidth - tol)
            {
                isGroupA = true;
                return true;
            }

            if (Math.Abs(rem - stepWidth * 0.5) < tol)
            {
                isGroupB = true;
                return true;
            }

            return false;
        }

        private static double PositiveMod(double value, double mod)
        {
            if (mod <= 0.0)
            {
                return 0.0;
            }

            double r = value % mod;
            if (r < 0.0)
            {
                r += mod;
            }
            return r;
        }

        private static XYZ GetCurveMidPoint(Curve c)
        {
            if (c == null)
            {
                return XYZ.Zero;
            }

            // Правильный вариант для normalized=true: параметр 0..1
            try
            {
                return c.Evaluate(0.5, true);
            }
            catch
            {
                // Фолбэк: через тесселяцию (самый стабильный для любых кривых/сегментов)
                try
                {
                    IList<XYZ> pts = c.Tessellate();
                    if (pts != null && pts.Count > 0)
                    {
                        return pts[pts.Count / 2];
                    }
                }
                catch
                {
                    // ignore
                }

                // Последний фолбэк: середина по концам
                try
                {
                    XYZ p0 = c.GetEndPoint(0);
                    XYZ p1 = c.GetEndPoint(1);
                    return (p0 + p1) * 0.5;
                }
                catch
                {
                    return XYZ.Zero;
                }
            }
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
            if (direction == null)
            {
                return null;
            }

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

                // Вертикальная плоскость => dot(Z) близко к 0
                double verticality = Math.Abs(n.DotProduct(XYZ.BasisZ));
                if (verticality > 0.2)
                {
                    continue;
                }

                // Совпадает с ориентацией стены
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
