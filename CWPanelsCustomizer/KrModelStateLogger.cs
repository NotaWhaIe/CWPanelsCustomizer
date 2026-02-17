using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CWPanelsCustomizer.Helpers;

namespace CWPanelsCustomizer
{
    [Transaction(TransactionMode.Manual)]
    public class KrModelStateLogger : IExternalCommand
    {
        public static string IS_TAB_NAME => "BIM";
        public static string IS_NAME => "Лог состояния модели";
        public static string IS_DESCRIPTION => "Записывает в лог текущее состояние модели: семейства, координаты, габариты, параметры";
        public static string IS_IMAGE => "CWPanelsCustomizer.Images.a1.png";

        private const double FEET_TO_MM = 304.8;
        private const string RACK_FAMILY_NAME = "КРСТ_НВФ_ZIAS_Стойка с кронштейнами в сборе_В2";

        // Больше этого порога — сводная строка, не перечисление
        private const int SUMMARY_THRESHOLD = 15;

        private Document _doc;
        private RevitLogger _logger;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            _doc = commandData.Application.ActiveUIDocument.Document;
            _logger = RevitLogger.GetLogger(_doc);
            _logger.BeginSession(IS_NAME, _doc.Title);

            try
            {
                Method();
                _logger.EndSession("Succeeded");
            }
            catch (Exception ex)
            {
                _logger.Error("FAILED", ex);
                _logger.EndSession("Failed");
                throw;
            }

            return Result.Succeeded;
        }

        private void Method()
        {
            Stopwatch sw = Stopwatch.StartNew();

            // Собрать все FamilyInstance — только те, что реально в пространстве модели
            // (имеют BoundingBox или LocationPoint)
            List<FamilyInstance> placed = new FilteredElementCollector(_doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => fi != null && fi.Symbol != null && IsInModelSpace(fi))
                .ToList();

            // Группировать по семейству
            var byFamily = placed
                .GroupBy(fi => fi.Symbol.FamilyName ?? "<no family>")
                .OrderBy(g => g.Key)
                .ToList();

            _logger.Info("Placed instances: " + placed.Count + "  Families: " + byFamily.Count);
            _logger.Info("---");

            int placedSkipped = 0;
            foreach (var group in byFamily)
            {
                string familyName = group.Key;
                List<FamilyInstance> instances = group.OrderBy(fi => fi.Id.IntegerValue).ToList();
                bool isRack = string.Equals(familyName, RACK_FAMILY_NAME, StringComparison.OrdinalIgnoreCase);

                if (isRack)
                {
                    LogFamilyDetailed(familyName, instances);
                }
                else if (instances.Count <= SUMMARY_THRESHOLD)
                {
                    LogFamilyCompact(familyName, instances);
                }
                else
                {
                    LogFamilySummary(familyName, instances);
                    placedSkipped += instances.Count;
                }
            }

            sw.Stop();
            _logger.LogSummary("Summary",
                ("PlacedTotal", placed.Count),
                ("Families", byFamily.Count),
                ("SummarizedCount", placedSkipped));
            _logger.Info("Execution time: " + sw.ElapsedMilliseconds + "ms");
        }

        /// <summary>
        /// Элемент реально размещён в пространстве модели, если имеет BB или LocationPoint.
        /// </summary>
        private bool IsInModelSpace(FamilyInstance fi)
        {
            if (fi.Location is LocationPoint || fi.Location is LocationCurve)
                return true;
            return fi.get_BoundingBox(null) != null;
        }

        /// <summary>
        /// Полный детальный лог — для стоек.
        /// </summary>
        private void LogFamilyDetailed(string familyName, List<FamilyInstance> instances)
        {
            _logger.Info("FAMILY [DETAIL]: '" + familyName + "'  count=" + instances.Count);

            foreach (FamilyInstance fi in instances)
            {
                XYZ loc = GetLocation(fi);
                BoundingBoxXYZ bb = fi.get_BoundingBox(null);

                string orientStr = GetOrientStr(fi);
                string profilStr = GetParamMm(fi, "Профиль_Длина");
                string massiStr  = GetParamMm(fi, "Массив_Длина");
                string bbSizeStr = bb != null ? SizeMm(bb) : "<no bb>";
                string bbRangeStr = bb != null
                    ? "Z=" + F0(bb.Min.Z) + ".." + F0(bb.Max.Z) + "mm"
                    : "";

                _logger.Info("  Id=" + fi.Id.IntegerValue
                    + " Type='" + (fi.Symbol?.Name ?? "") + "'"
                    + " Loc=" + FmtXyzMm(loc)
                    + " Orient=" + orientStr
                    + " Профиль_Длина=" + profilStr
                    + " Массив_Длина=" + massiStr
                    + " Size=" + bbSizeStr
                    + " " + bbRangeStr);
            }

            _logger.Info("---");
        }

        /// <summary>
        /// Компактный лог — по одной строке на экземпляр, без лишних полей.
        /// </summary>
        private void LogFamilyCompact(string familyName, List<FamilyInstance> instances)
        {
            _logger.Info("FAMILY [COMPACT]: '" + familyName + "'  count=" + instances.Count);

            foreach (FamilyInstance fi in instances)
            {
                XYZ loc = GetLocation(fi);
                BoundingBoxXYZ bb = fi.get_BoundingBox(null);
                string locStr = loc != null ? FmtXyzMm(loc) : "<no loc>";
                string sizeStr = bb != null ? SizeMm(bb) : "<no bb>";
                string zRangeStr = bb != null
                    ? " Z=" + F0(bb.Min.Z) + ".." + F0(bb.Max.Z) + "mm"
                    : "";

                _logger.Info("  Id=" + fi.Id.IntegerValue
                    + " Type='" + (fi.Symbol?.Name ?? "") + "'"
                    + " Loc=" + locStr
                    + " Size=" + sizeStr
                    + zRangeStr);
            }

            _logger.Info("---");
        }

        /// <summary>
        /// Сводная строка — одна на всю группу, для многочисленных однотипных элементов.
        /// </summary>
        private void LogFamilySummary(string familyName, List<FamilyInstance> instances)
        {
            double minZ = double.MaxValue, maxZ = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            int withBb = 0;

            foreach (FamilyInstance fi in instances)
            {
                BoundingBoxXYZ bb = fi.get_BoundingBox(null);
                if (bb == null) continue;
                withBb++;
                minZ = Math.Min(minZ, bb.Min.Z * FEET_TO_MM);
                maxZ = Math.Max(maxZ, bb.Max.Z * FEET_TO_MM);
                minY = Math.Min(minY, bb.Min.Y * FEET_TO_MM);
                maxY = Math.Max(maxY, bb.Max.Y * FEET_TO_MM);
            }

            // Уникальные типы с количеством
            var typeCounts = instances
                .GroupBy(fi => fi.Symbol?.Name ?? "<no type>")
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key + "×" + g.Count());
            string typesStr = string.Join(", ", typeCounts);

            string rangeStr = withBb > 0
                ? " Z=" + minZ.ToString("F0") + ".." + maxZ.ToString("F0") + "mm"
                + " Y=" + minY.ToString("F0") + ".." + maxY.ToString("F0") + "mm"
                : "";

            _logger.Info("FAMILY [SUMMARY]: '" + familyName + "'"
                + "  count=" + instances.Count + rangeStr
                + "  types=[" + typesStr + "]");
        }

        // ── Вспомогательные ────────────────────────────────────────────────

        private XYZ GetLocation(FamilyInstance fi)
        {
            if (fi.Location is LocationPoint lp) return lp.Point;
            if (fi.Location is LocationCurve lc && lc.Curve != null) return lc.Curve.GetEndPoint(0);
            return null;
        }

        private string GetOrientStr(FamilyInstance fi)
        {
            try
            {
                XYZ f = fi.FacingOrientation;
                XYZ h = fi.HandOrientation;
                return "F" + FmtDir(f) + " H" + FmtDir(h);
            }
            catch { return "?"; }
        }

        private string GetParamMm(FamilyInstance fi, string name)
        {
            Parameter p = fi.LookupParameter(name);
            if (p == null || p.StorageType != StorageType.Double) return "<n/a>";
            return (p.AsDouble() * FEET_TO_MM).ToString("F0") + "mm";
        }

        private string FmtXyzMm(XYZ p)
        {
            if (p == null) return "<null>";
            return "(" + F0(p.X) + ", " + F0(p.Y) + ", " + F0(p.Z) + ")mm";
        }

        private string FmtDir(XYZ v)
        {
            if (v == null) return "(null)";
            return "(" + v.X.ToString("F2") + "," + v.Y.ToString("F2") + "," + v.Z.ToString("F2") + ")";
        }

        private string SizeMm(BoundingBoxXYZ bb)
        {
            double sx = (bb.Max.X - bb.Min.X) * FEET_TO_MM;
            double sy = (bb.Max.Y - bb.Min.Y) * FEET_TO_MM;
            double sz = (bb.Max.Z - bb.Min.Z) * FEET_TO_MM;
            return sx.ToString("F0") + "×" + sy.ToString("F0") + "×" + sz.ToString("F0") + "mm";
        }

        private string F0(double feet) => (feet * FEET_TO_MM).ToString("F0");
    }
}
