using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using CWPanelsCustomizer.Helpers;

namespace CWPanelsCustomizer
{
    [Transaction(TransactionMode.Manual)]
    public class KrMullionPlacement : IExternalCommand
    {
        public static string IS_TAB_NAME => "КР";
        public static string IS_NAME => "Разместить стойки по витражу";
        public static string IS_DESCRIPTION => "Размещение семейства стоек по вертикальным линиям витража на поверхности стены";
        public static string IS_IMAGE => "CWPanelsCustomizer.Images.a1.png";

        private SphereByPoint _sphereByPoint;
        private UIDocument _uidoc;
        private Document _doc;
        private RevitLogger _logger;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            _uidoc = commandData.Application.ActiveUIDocument;
            _doc = _uidoc.Document;
            _sphereByPoint = new SphereByPoint(_doc);

            _logger = RevitLogger.GetLogger(_doc);
            _logger.BeginSession(IS_NAME, _doc.Title);

            using (TransactionGroup tg = new TransactionGroup(_doc, IS_NAME))
            {
                tg.Start();

                try
                {
                    Method();
                    tg.Assimilate();
                    _logger.EndSession("Succeeded");
                }
                catch (Exception ex)
                {
                    _logger.Error("FAILED", ex);
                    _logger.EndSession("Failed");
                    throw;
                }
            }

            return Result.Succeeded;
        }
        /// <summary>
        /// Осноснвая логика команды
        /// </summary>
        /// <exception cref="InvalidOperationException"></exception>
        private void Method()
        {
            View activeView = _doc.ActiveView;
            View3D view3D = activeView as View3D;
            if (view3D == null)
            {
                _logger.Error("ERROR: Активный вид не является 3D видом.");
                throw new InvalidOperationException("Команда должна запускаться на 3D виде.");
            }

            SketchPlane sketchPlane = view3D.SketchPlane;
            if (sketchPlane == null)
            {
                _logger.Error("ERROR: На 3D виде не задана рабочая плоскость (SketchPlane == null).");
                throw new InvalidOperationException("На активном 3D виде должна быть заранее настроена рабочая плоскость.");
            }

            Plane workPlane = sketchPlane.GetPlane();
            _logger.Info("WorkPlane: Origin=" + FormatXyz(workPlane.Origin) + " Normal=" + FormatXyz(workPlane.Normal));

            const string familyName = "КРСТ_НВФ_ZIAS_Массив стоек с кронштейнами_В2";
            const string symbolName = "187";

            FamilySymbol symbol = FindFamilySymbolByNames(_doc, familyName, symbolName);
            if (symbol == null)
            {
                _logger.Error("ERROR: Не найден FamilySymbol. FamilyName='" + familyName + "', TypeName='" + symbolName + "'.");
                throw new InvalidOperationException("Не найдено семейство/тип: " + familyName + " : " + symbolName);
            }

            _logger.Info("FamilySymbol: SymbolId=" + symbol.Id.IntegerValue + ", Family='" + symbol.FamilyName + "', Type='" + symbol.Name + "'");

            List<Wall> allCurtainWalls = new FilteredElementCollector(_doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .Where(w => w != null && w.CurtainGrid != null)
                .ToList();

            _logger.Info("CurtainWalls found: " + allCurtainWalls.Count);

            Wall targetWall = FindNearestCurtainWallToPlane(allCurtainWalls, workPlane);
            if (targetWall == null)
            {
                _logger.Error("ERROR: Не найден витраж (CurtainWall) рядом с рабочей плоскостью.");
                throw new InvalidOperationException("Не найден витраж (CurtainWall) рядом с рабочей плоскостью.");
            }

            _logger.Info("Target curtain wall: Id=" + targetWall.Id.IntegerValue + ", Name=" + targetWall.Name);

            CurtainGrid curtainGrid = targetWall.CurtainGrid;
            if (curtainGrid == null)
            {
                _logger.Error("ERROR: У выбранного витража CurtainGrid == null.");
                throw new InvalidOperationException("У выбранного витража отсутствует CurtainGrid.");
            }

            List<CurtainGridLine> allGridLines = GetAllCurtainGridLines(_doc, curtainGrid);
            _logger.Info("CurtainGrid lines (U+V) total: " + allGridLines.Count);

            List<CurtainGridLine> verticalGridLines = allGridLines
                .Where(gl => gl != null)
                .Where(gl =>
                {
                    Curve c = GetGridLineCurve(gl);
                    if (c == null) return false;
                    XYZ dir = GetCurveDirection(c);
                    double verticality = Math.Abs(dir.Normalize().DotProduct(XYZ.BasisZ));
                    return verticality >= 0.99;
                })
                .ToList();

            _logger.Info("Vertical grid lines: " + verticalGridLines.Count);

            // Нижний обрез витража по геометрии (wall -> panels/mullions -> bb fallback)
            double minZ = GetCurtainWallBottomZ_ByGeometry(_doc, targetWall, curtainGrid, activeView);
            _logger.Info("CurtainWall bottom Z (geometry-based): " + minZ.ToString("F6"));

            // 1 мм в футах
            double toleranceFeet = 0.00328084;

            using (Transaction t = new Transaction(_doc, "Place racks along vertical curtain grid (from bottom)"))
            {
                t.Start();

                FailureHandlingOptions fho = t.GetFailureHandlingOptions();
                fho.SetFailuresPreprocessor(new DuplicateInstancesWarningSuppressor(_logger));
                t.SetFailureHandlingOptions(fho);

                if (!symbol.IsActive)
                {
                    _logger.Info("Symbol не активен -> Activate()");
                    symbol.Activate();
                    _doc.Regenerate();
                }

                int created = 0;
                int skipped = 0;

                for (int i = 0; i < verticalGridLines.Count; i++)
                {
                    CurtainGridLine gridLine = verticalGridLines[i];
                    Curve curve = GetGridLineCurve(gridLine);
                    if (curve == null)
                    {
                        _logger.Debug("GridLine[" + i + "]: curve is null -> skip");
                        skipped++;
                        continue;
                    }

                    XYZ mid = GetCurveMidPoint(curve);
                    XYZ rawPoint = new XYZ(mid.X, mid.Y, minZ);
                    XYZ projected = ProjectPointToPlane(rawPoint, workPlane);

                    ElementId existingId = FindDuplicateInstanceIdByEffectivePosition(_doc, symbol, ElementId.InvalidElementId, projected, toleranceFeet);
                    if (existingId != ElementId.InvalidElementId)
                    {
                        _logger.Info("GridLine[" + i + "] Id=" + gridLine.Id.IntegerValue + " → SKIPPED (existing=" + existingId.IntegerValue + ") Pos=" + FormatXyz(projected));
                        skipped++;
                        continue;
                    }

                    FamilyInstance newInstance = _doc.Create.NewFamilyInstance(projected, symbol, sketchPlane, StructuralType.NonStructural);
                    _logger.Info("GridLine[" + i + "] Id=" + gridLine.Id.IntegerValue + " → Created InstanceId=" + newInstance.Id.IntegerValue + " Pos=" + FormatXyz(projected));
                    created++;
                }

                _logger.LogSummary("Placement result", ("Created", created), ("SkippedOrDeleted", skipped));

                t.Commit();
            }

        }

        // ==========================================================
        // GEOMETRY BOTTOM Z: WALL SOLID -> PANELS/MULLIONS SOLIDS -> BB FALLBACK
        // ==========================================================
        /// <summary>
        /// 
        /// </summary>
        /// <param name="doc"></param>
        /// <param name="wall"></param>
        /// <param name="grid"></param>
        /// <param name="viewForOptions"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        private double GetCurtainWallBottomZ_ByGeometry(Document doc, Wall wall, CurtainGrid grid, View viewForOptions)
        {
            if (TryGetElementMinZBySolid(doc, wall, viewForOptions, out double minZWall))
            {
                return minZWall;
            }

            List<ElementId> idsToCheck = new List<ElementId>();

            try
            {
                ICollection<ElementId> panelIds = grid.GetPanelIds();
                if (panelIds != null && panelIds.Count > 0) idsToCheck.AddRange(panelIds);
            }
            catch (Exception ex)
            {
                _logger.Error("GetCurtainWallBottomZ_ByGeometry: grid.GetPanelIds() exception", ex);
            }

            try
            {
                ICollection<ElementId> mullionIds = grid.GetMullionIds();
                if (mullionIds != null && mullionIds.Count > 0) idsToCheck.AddRange(mullionIds);
            }
            catch (Exception ex)
            {
                _logger.Error("GetCurtainWallBottomZ_ByGeometry: grid.GetMullionIds() exception", ex);
            }

            double bestMinZ = double.MaxValue;
            int successCount = 0;

            for (int i = 0; i < idsToCheck.Count; i++)
            {
                Element e = doc.GetElement(idsToCheck[i]);
                if (e == null) continue;

                if (TryGetElementMinZBySolid(doc, e, viewForOptions, out double z))
                {
                    successCount++;
                    if (z < bestMinZ) bestMinZ = z;
                }
            }

            if (successCount > 0 && bestMinZ != double.MaxValue)
            {
                return bestMinZ;
            }

            BoundingBoxXYZ bb = wall.get_BoundingBox(null);
            if (bb != null && bb.Min != null)
            {
                _logger.Warn("GetCurtainWallBottomZ: используем FALLBACK BoundingBox.Min.Z=" + bb.Min.Z.ToString("F6"));
                return bb.Min.Z;
            }

            throw new InvalidOperationException("Не удалось определить нижний обрез витража: нет точек по Solid у стены/панелей/импостов и нет BoundingBox.");
        }

        private bool TryGetElementMinZBySolid(Document doc, Element element, View viewForOptions, out double minZ)
        {
            minZ = double.MaxValue;

            if (doc == null || element == null) return false;

            Options opt = new Options();
            opt.ComputeReferences = false;
            opt.IncludeNonVisibleObjects = true;

            // КРИТИЧНО: нельзя одновременно opt.View и opt.DetailLevel
            if (viewForOptions != null)
            {
                opt.View = viewForOptions; // view-specific geometry
            }
            else
            {
                opt.DetailLevel = ViewDetailLevel.Fine;
            }

            GeometryElement geom;
            try
            {
                geom = element.get_Geometry(opt);
            }
            catch (Exception ex)
            {
                _logger.Error("TryGetElementMinZBySolid: get_Geometry exception for ElementId=" + element.Id.IntegerValue, ex);
                return false;
            }

            if (geom == null) return false;

            int solidsCount = 0;
            int pointsCount = 0;

            CollectMinZFromGeometry(geom, Transform.Identity, ref solidsCount, ref pointsCount, ref minZ);

            return pointsCount > 0 && minZ != double.MaxValue;
        }

        private void CollectMinZFromGeometry(GeometryElement geom, Transform currentTransform, ref int solidsCount, ref int pointsCount, ref double minZ)
        {
            foreach (GeometryObject obj in geom)
            {
                if (obj == null) continue;

                Solid solid = obj as Solid;
                if (solid != null)
                {
                    if (solid.Volume > 1e-9)
                    {
                        solidsCount++;
                        UpdateMinZFromSolid(solid, currentTransform, ref pointsCount, ref minZ);
                    }
                    continue;
                }

                GeometryInstance inst = obj as GeometryInstance;
                if (inst != null)
                {
                    Transform t = inst.Transform;
                    Transform next = currentTransform;
                    if (t != null) next = currentTransform.Multiply(t);

                    GeometryElement instGeom = inst.GetInstanceGeometry();
                    if (instGeom != null)
                    {
                        CollectMinZFromGeometry(instGeom, next, ref solidsCount, ref pointsCount, ref minZ);
                    }
                    continue;
                }
            }
        }

        private void UpdateMinZFromSolid(Solid solid, Transform transform, ref int pointsCount, ref double minZ)
        {
            if (solid == null) return;

            bool gotAnyPoint = false;

            FaceArray faces = solid.Faces;
            if (faces != null)
            {
                foreach (Face face in faces)
                {
                    if (face == null) continue;

                    Mesh mesh;
                    try
                    {
                        mesh = face.Triangulate();
                    }
                    catch
                    {
                        continue;
                    }

                    if (mesh == null) continue;

                    IList<XYZ> vertices = mesh.Vertices;
                    if (vertices == null || vertices.Count == 0) continue;

                    for (int i = 0; i < vertices.Count; i++)
                    {
                        XYZ v = vertices[i];
                        if (v == null) continue;

                        XYZ p = (transform != null) ? transform.OfPoint(v) : v;

                        if (p.Z < minZ) minZ = p.Z;
                        pointsCount++;
                        gotAnyPoint = true;
                    }
                }
            }

            if (!gotAnyPoint)
            {
                EdgeArray edges = solid.Edges;
                if (edges != null && edges.Size > 0)
                {
                    for (int ei = 0; ei < edges.Size; ei++)
                    {
                        Edge e = edges.get_Item(ei);
                        if (e == null) continue;

                        IList<XYZ> pts;
                        try
                        {
                            pts = e.Tessellate();
                        }
                        catch
                        {
                            continue;
                        }

                        if (pts == null || pts.Count == 0) continue;

                        for (int pi = 0; pi < pts.Count; pi++)
                        {
                            XYZ v = pts[pi];
                            if (v == null) continue;

                            XYZ p = (transform != null) ? transform.OfPoint(v) : v;

                            if (p.Z < minZ) minZ = p.Z;
                            pointsCount++;
                            gotAnyPoint = true;
                        }
                    }
                }
            }

        }

        private Wall FindNearestCurtainWallToPlane(List<Wall> curtainWalls, Plane plane)
        {
            if (curtainWalls == null || curtainWalls.Count == 0) return null;
            if (plane == null) return null;

            Wall best = null;
            double bestDist = double.MaxValue;

            foreach (Wall w in curtainWalls)
            {
                BoundingBoxXYZ bb = w.get_BoundingBox(null);
                if (bb == null || bb.Min == null || bb.Max == null) continue;

                XYZ center = (bb.Min + bb.Max) * 0.5;
                double signed = plane.Normal.DotProduct(center - plane.Origin);
                double dist = Math.Abs(signed);

                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = w;
                }
            }

            if (best != null)
            {
                _logger.Info("Nearest curtain wall chosen: Id=" + best.Id.IntegerValue + ", DistToPlaneFt=" + bestDist.ToString("F6"));
            }

            return best;
        }

        private List<CurtainGridLine> GetAllCurtainGridLines(Document doc, CurtainGrid grid)
        {
            List<CurtainGridLine> result = new List<CurtainGridLine>();
            if (doc == null || grid == null) return result;

            ICollection<ElementId> uIds = grid.GetUGridLineIds();
            ICollection<ElementId> vIds = grid.GetVGridLineIds();

            if (uIds != null)
            {
                foreach (ElementId id in uIds)
                {
                    CurtainGridLine gl = doc.GetElement(id) as CurtainGridLine;
                    if (gl != null) result.Add(gl);
                }
            }

            if (vIds != null)
            {
                foreach (ElementId id in vIds)
                {
                    CurtainGridLine gl = doc.GetElement(id) as CurtainGridLine;
                    if (gl != null) result.Add(gl);
                }
            }

            return result;
        }

        private Curve GetGridLineCurve(CurtainGridLine gridLine)
        {
            if (gridLine == null) return null;
            try { return gridLine.FullCurve; } catch { return null; }
        }

        private XYZ GetCurveDirection(Curve curve)
        {
            if (curve == null) return XYZ.BasisX;

            XYZ p0 = curve.GetEndPoint(0);
            XYZ p1 = curve.GetEndPoint(1);
            XYZ v = p1 - p0;

            if (v.IsZeroLength()) return XYZ.BasisX;
            return v.Normalize();
        }

        private XYZ GetCurveMidPoint(Curve curve)
        {
            if (curve == null) return XYZ.Zero;

            try
            {
                if (curve.IsBound) return curve.Evaluate(0.5, true);
            }
            catch
            {
            }

            XYZ p0 = curve.GetEndPoint(0);
            XYZ p1 = curve.GetEndPoint(1);
            return (p0 + p1) * 0.5;
        }

        private XYZ ProjectPointToPlane(XYZ point, Plane plane)
        {
            if (point == null || plane == null) return point;
            XYZ v = point - plane.Origin;
            double d = plane.Normal.DotProduct(v);
            return point - plane.Normal * d;
        }

        private XYZ GetInstanceEffectivePosition(FamilyInstance instance)
        {
            if (instance == null) return null;

            LocationPoint lp = instance.Location as LocationPoint;
            if (lp != null && lp.Point != null) return lp.Point;

            BoundingBoxXYZ bb = instance.get_BoundingBox(null);
            if (bb != null && bb.Min != null && bb.Max != null) return (bb.Min + bb.Max) * 0.5;

            return XYZ.Zero;
        }

        private ElementId FindDuplicateInstanceIdByEffectivePosition(Document doc, FamilySymbol symbol, ElementId excludeInstanceId, XYZ targetPos, double toleranceFeet)
        {
            if (doc == null || symbol == null || targetPos == null) return ElementId.InvalidElementId;

            FilteredElementCollector collector = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance));

            foreach (FamilyInstance fi in collector)
            {
                if (fi == null) continue;
                if (fi.Id == excludeInstanceId) continue;

                FamilySymbol fiSymbol = fi.Symbol;
                if (fiSymbol == null) continue;
                if (fiSymbol.Id.IntegerValue != symbol.Id.IntegerValue) continue;

                XYZ pos = GetInstanceEffectivePosition(fi);
                if (pos == null) continue;

                double dist = pos.DistanceTo(targetPos);
                if (dist <= toleranceFeet) return fi.Id;
            }

            return ElementId.InvalidElementId;
        }

        private class DuplicateInstancesWarningSuppressor : IFailuresPreprocessor
        {
            private readonly RevitLogger _logger;

            public DuplicateInstancesWarningSuppressor(RevitLogger logger)
            {
                _logger = logger;
            }

            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                IList<FailureMessageAccessor> failures = failuresAccessor.GetFailureMessages();
                if (failures == null || failures.Count == 0) return FailureProcessingResult.Continue;

                int suppressedCount = 0;

                foreach (FailureMessageAccessor fma in failures)
                {
                    if (fma == null) continue;

                    string text = fma.GetDescriptionText();
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    if (text.Contains("В одном и том же месте имеются идентичные экземпляры"))
                    {
                        suppressedCount++;
                        failuresAccessor.DeleteWarning(fma);
                    }
                }

                if (suppressedCount > 0)
                {
                    _logger.Debug("SUPPRESS WARNING x" + suppressedCount + ": В одном и том же месте имеются идентичные экземпляры.");
                }

                return FailureProcessingResult.Continue;
            }
        }

        private FamilySymbol FindFamilySymbolByNames(Document doc, string familyName, string symbolName)
        {
            FilteredElementCollector collector = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol));

            foreach (FamilySymbol fs in collector)
            {
                if (string.Equals(fs.FamilyName, familyName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(fs.Name, symbolName, StringComparison.OrdinalIgnoreCase))
                {
                    return fs;
                }
            }

            return null;
        }

        private string FormatXyz(XYZ p)
        {
            if (p == null) return "<null>";
            return "(" + p.X.ToString("F3") + ", " + p.Y.ToString("F3") + ", " + p.Z.ToString("F3") + ")";
        }
    }

    internal static class XyzExtensions
    {
        internal static bool IsZeroLength(this XYZ v)
        {
            if (v == null) return true;
            return v.GetLength() < 1e-9;
        }
    }
}
