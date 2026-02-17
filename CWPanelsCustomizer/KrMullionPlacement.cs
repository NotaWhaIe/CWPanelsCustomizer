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

            // Удалить все существующие экземпляры этого FamilySymbol перед размещением
            int deleted = DeleteExistingInstances(_doc, symbol);
            if (deleted > 0)
            {
                _logger.Info("Deleted " + deleted + " existing instances of " + symbol.FamilyName + " : " + symbol.Name);
            }

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

                for (int i = 0; i < verticalGridLines.Count; i++)
                {
                    CurtainGridLine gridLine = verticalGridLines[i];
                    Curve curve = GetGridLineCurve(gridLine);
                    if (curve == null)
                    {
                        _logger.Debug("GridLine[" + i + "]: curve is null -> skip");
                        continue;
                    }

                    XYZ bottomPt = GetCurveBottomPoint(curve);
                    XYZ rawPoint = new XYZ(bottomPt.X, bottomPt.Y, bottomPt.Z);
                    XYZ projected = ProjectPointToPlane(rawPoint, workPlane);

                    _logger.Debug("GridLine[" + i + "] Id=" + gridLine.Id.IntegerValue + " bottomPt.Z=" + bottomPt.Z.ToString("F6") + " projected=" + FormatXyz(projected));

                    FamilyInstance newInstance = _doc.Create.NewFamilyInstance(projected, symbol, sketchPlane, StructuralType.NonStructural);
                    _logger.Info("GridLine[" + i + "] Id=" + gridLine.Id.IntegerValue + " → Created InstanceId=" + newInstance.Id.IntegerValue + " Pos=" + FormatXyz(projected));
                    created++;
                }

                _logger.LogSummary("Placement result", ("Deleted", deleted), ("Created", created));

                t.Commit();
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

        private XYZ GetCurveBottomPoint(Curve curve)
        {
            if (curve == null) return XYZ.Zero;
            XYZ p0 = curve.GetEndPoint(0);
            XYZ p1 = curve.GetEndPoint(1);
            return p0.Z <= p1.Z ? p0 : p1;
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

        private XYZ ProjectPointToPlane(XYZ point, Plane plane)
        {
            if (point == null || plane == null) return point;
            XYZ v = point - plane.Origin;
            double d = plane.Normal.DotProduct(v);
            return point - plane.Normal * d;
        }

        private int DeleteExistingInstances(Document doc, FamilySymbol symbol)
        {
            if (doc == null || symbol == null) return 0;

            List<ElementId> toDelete = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => fi.Symbol != null && fi.Symbol.Id.IntegerValue == symbol.Id.IntegerValue)
                .Select(fi => fi.Id)
                .ToList();

            if (toDelete.Count == 0) return 0;

            using (Transaction t = new Transaction(doc, "Delete existing rack instances"))
            {
                t.Start();
                doc.Delete(toDelete);
                t.Commit();
            }

            return toDelete.Count;
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

                    FailureSeverity severity = fma.GetSeverity();
                    string text = fma.GetDescriptionText();

                    // Логируем ВСЁ: текст реального сообщения виден в логе независимо от языка Revit
                    _logger.Info("REVIT FAILURE [" + severity + "]: " + text);

                    if (severity == FailureSeverity.Warning)
                    {
                        failuresAccessor.DeleteWarning(fma);
                        suppressedCount++;
                    }
                }

                if (suppressedCount > 0)
                {
                    _logger.Info("SUPPRESS WARNING x" + suppressedCount + " (deleted)");
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
