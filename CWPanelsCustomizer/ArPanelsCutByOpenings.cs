using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace CWPanelsCustomizer.Helpers
{
    [Transaction(TransactionMode.Manual)]
    public class ArPanelsCutByOpenings : IExternalCommand
    {
        public static string IS_TAB_NAME => "АР";
        public static string IS_NAME => "Подрезать АР кассеты по окнам";
        public static string IS_DESCRIPTION => "Автоматизирует Revit 'Cut Geometry': витражная панель-стена (Wall из Панели витража) режется окном KRST_Окно_П_205. Void-cut через InstanceVoidCutUtils. Есть защита от повторных резов.";
        public static string IS_IMAGE => "CWPanelsCustomizer.Images.a1.png";

        private const string TARGET_WINDOW_FAMILY_OR_TYPE_NAME = "KRST_Окно_П_205";

        private const double BBOX_TOL_MM = 10.0;
        private const double MM_TO_FT = 1.0 / 304.8;

        private SphereByPoint _sphereByPoint;
        private UIDocument _uidoc;
        private Document _doc;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            _uidoc = commandData.Application.ActiveUIDocument;
            _doc = _uidoc.Document;
            _sphereByPoint = new SphereByPoint(_doc);

            Debug.WriteLine("==============================================");
            Debug.WriteLine($"[{IS_NAME}] START. Document: {_doc.Title}");

            try
            {
                using (TransactionGroup tg = new TransactionGroup(_doc, IS_NAME))
                {
                    tg.Start();
                    Method();
                    tg.Assimilate();
                }

                Debug.WriteLine($"[{IS_NAME}] DONE OK.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{IS_NAME}] FATAL ERROR: {ex}");
                message = ex.Message;
                return Result.Failed;
            }
            finally
            {
                Debug.WriteLine($"[{IS_NAME}] END.");
                Debug.WriteLine("==============================================");
            }
        }

        private void Method()
        {
            List<FamilyInstance> windows = CollectTargetWindows();
            List<Wall> panelWalls = CollectCurtainPanelWalls();

            Debug.WriteLine($"[{IS_NAME}] Target windows found: {windows.Count}");
            Debug.WriteLine($"[{IS_NAME}] Curtain panel walls found: {panelWalls.Count}");

            if (windows.Count == 0 || panelWalls.Count == 0)
            {
                Debug.WriteLine($"[{IS_NAME}] Nothing to do.");
                return;
            }

            double tolFt = BBOX_TOL_MM * MM_TO_FT;

            int missingOrInvalidBbox = 0;
            int intersectPairs = 0;

            int alreadyCut = 0;
            int cutSuccess = 0;
            int cutSkippedWallNotSupported = 0;
            int cutFailed = 0;

            using (Transaction t = new Transaction(_doc, "Cut Geometry (void cut) wall panels by windows"))
            {
                t.Start();

                foreach (Wall panelWall in panelWalls)
                {
                    BoundingBoxXYZ wallBbox = panelWall.get_BoundingBox(null);
                    Outline wallOutline = ToOutlineSafe(wallBbox, tolFt);
                    if (wallOutline == null)
                    {
                        missingOrInvalidBbox++;
                        Debug.WriteLine($"[{IS_NAME}] Wall bbox/outline invalid. WallId={panelWall.Id.IntegerValue}");
                        continue;
                    }

                    if (!TryCanBeCutWithVoid(panelWall))
                    {
                        cutSkippedWallNotSupported++;
                        Debug.WriteLine($"[{IS_NAME}] Wall cannot be cut with void. WallId={panelWall.Id.IntegerValue}");
                        continue;
                    }

                    foreach (FamilyInstance window in windows)
                    {
                        BoundingBoxXYZ winBbox = window.get_BoundingBox(null);
                        Outline winOutline = ToOutlineSafe(winBbox, tolFt);
                        if (winOutline == null)
                        {
                            missingOrInvalidBbox++;
                            Debug.WriteLine($"[{IS_NAME}] Window bbox/outline invalid. WindowId={window.Id.IntegerValue}");
                            continue;
                        }

                        if (!OutlinesIntersect(wallOutline, winOutline))
                        {
                            continue;
                        }

                        intersectPairs++;
                        Debug.WriteLine($"[{IS_NAME}] INTERSECT: WallId={panelWall.Id.IntegerValue} <-> WindowId={window.Id.IntegerValue}");

                        try
                        {
                            // ✅ Защита от повторных резов: если связь уже есть — пропускаем
                            if (TryIsVoidInstanceCuttingElement(panelWall, window))
                            {
                                alreadyCut++;
                                Debug.WriteLine($"[{IS_NAME}] SKIP (already cut). WallId={panelWall.Id.IntegerValue} already cut by WindowId={window.Id.IntegerValue}");
                                continue;
                            }

                            if (TryAddInstanceVoidCut(_doc, panelWall, window))
                            {
                                cutSuccess++;
                                Debug.WriteLine($"[{IS_NAME}] CUT OK (InstanceVoidCut). WallId={panelWall.Id.IntegerValue} cut by WindowId={window.Id.IntegerValue}");
                            }
                            else
                            {
                                cutFailed++;
                                Debug.WriteLine($"[{IS_NAME}] CUT FAILED (no matching API signature). WallId={panelWall.Id.IntegerValue}, WindowId={window.Id.IntegerValue}");
                            }
                        }
                        catch (Exception ex)
                        {
                            cutFailed++;
                            Debug.WriteLine($"[{IS_NAME}] CUT EXCEPTION. WallId={panelWall.Id.IntegerValue}, WindowId={window.Id.IntegerValue}. Error: {ex.Message}");
                            Debug.WriteLine(ex.ToString());
                        }
                    }
                }

                t.Commit();
            }

            Debug.WriteLine($"[{IS_NAME}] SUMMARY:");
            Debug.WriteLine($"[{IS_NAME}]  Intersect pairs: {intersectPairs}");
            Debug.WriteLine($"[{IS_NAME}]  Cut success: {cutSuccess}");
            Debug.WriteLine($"[{IS_NAME}]  Skipped (already cut): {alreadyCut}");
            Debug.WriteLine($"[{IS_NAME}]  Skipped (wall not supported for void cut): {cutSkippedWallNotSupported}");
            Debug.WriteLine($"[{IS_NAME}]  Failed: {cutFailed}");
            Debug.WriteLine($"[{IS_NAME}]  Missing/invalid bbox: {missingOrInvalidBbox}");
        }

        private List<FamilyInstance> CollectTargetWindows()
        {
            List<FamilyInstance> allWindows = new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_Windows)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .ToList();

            List<FamilyInstance> result = new List<FamilyInstance>();

            foreach (FamilyInstance w in allWindows)
            {
                string familyName = SafeString(() => w.Symbol?.Family?.Name);
                string typeName = SafeString(() => w.Symbol?.Name);
                string instanceName = SafeString(() => w.Name);

                bool match =
                    StringEqualsIgnoreCase(familyName, TARGET_WINDOW_FAMILY_OR_TYPE_NAME) ||
                    StringEqualsIgnoreCase(typeName, TARGET_WINDOW_FAMILY_OR_TYPE_NAME) ||
                    StringEqualsIgnoreCase(instanceName, TARGET_WINDOW_FAMILY_OR_TYPE_NAME);

                if (match)
                {
                    Debug.WriteLine($"[{IS_NAME}] Target window: Id={w.Id.IntegerValue}, Family='{familyName}', Type='{typeName}', Name='{instanceName}'");
                    result.Add(w);
                }
            }

            return result;
        }

        private List<Wall> CollectCurtainPanelWalls()
        {
            List<Element> elems = new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_CurtainWallPanels)
                .WhereElementIsNotElementType()
                .ToElements()
                .ToList();

            List<Wall> walls = new List<Wall>();
            foreach (Element e in elems)
            {
                if (e is Wall w)
                {
                    walls.Add(w);
                }
            }

            return walls;
        }

        // -----------------------------
        // InstanceVoidCutUtils helpers (reflection-safe)
        // -----------------------------

        private static bool TryCanBeCutWithVoid(Element host)
        {
            try
            {
                Type t = typeof(InstanceVoidCutUtils);
                MethodInfo mi = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "CanBeCutWithVoid" && m.GetParameters().Length == 1);

                if (mi == null)
                {
                    return true;
                }

                object res = mi.Invoke(null, new object[] { host });
                return res is bool b && b;
            }
            catch
            {
                return true;
            }
        }

        private static bool TryIsVoidInstanceCuttingElement(Element host, FamilyInstance cutter)
        {
            try
            {
                Type t = typeof(InstanceVoidCutUtils);
                MethodInfo mi = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "IsVoidInstanceCuttingElement" && m.GetParameters().Length == 2);

                if (mi == null)
                {
                    return false;
                }

                object res = mi.Invoke(null, new object[] { host, cutter });
                return res is bool b && b;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryAddInstanceVoidCut(Document doc, Element host, FamilyInstance cutter)
        {
            Type t = typeof(InstanceVoidCutUtils);
            MethodInfo[] methods = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "AddInstanceVoidCut")
                .ToArray();

            foreach (MethodInfo mi in methods)
            {
                ParameterInfo[] ps = mi.GetParameters();

                try
                {
                    if (ps.Length == 3 &&
                        ps[0].ParameterType == typeof(Document) &&
                        ps[1].ParameterType == typeof(Element) &&
                        typeof(Element).IsAssignableFrom(ps[2].ParameterType))
                    {
                        mi.Invoke(null, new object[] { doc, host, cutter });
                        return true;
                    }

                    if (ps.Length == 3 &&
                        ps[0].ParameterType == typeof(Document) &&
                        ps[1].ParameterType == typeof(ElementId) &&
                        ps[2].ParameterType == typeof(ElementId))
                    {
                        mi.Invoke(null, new object[] { doc, host.Id, cutter.Id });
                        return true;
                    }

                    if (ps.Length == 3 &&
                        ps[0].ParameterType == typeof(Document) &&
                        typeof(Element).IsAssignableFrom(ps[1].ParameterType) &&
                        typeof(Element).IsAssignableFrom(ps[2].ParameterType))
                    {
                        mi.Invoke(null, new object[] { doc, host, cutter });
                        return true;
                    }
                }
                catch
                {
                    // пробуем следующий overload
                }
            }

            return false;
        }

        // -----------------------------
        // BBox helpers
        // -----------------------------

        private static Outline ToOutlineSafe(BoundingBoxXYZ bbox, double tolFt)
        {
            if (bbox == null || bbox.Min == null || bbox.Max == null)
            {
                return null;
            }

            if (!IsFinite(bbox.Min.X) || !IsFinite(bbox.Min.Y) || !IsFinite(bbox.Min.Z) ||
                !IsFinite(bbox.Max.X) || !IsFinite(bbox.Max.Y) || !IsFinite(bbox.Max.Z))
            {
                return null;
            }

            double minX = bbox.Min.X - tolFt;
            double minY = bbox.Min.Y - tolFt;
            double minZ = bbox.Min.Z - tolFt;

            double maxX = bbox.Max.X + tolFt;
            double maxY = bbox.Max.Y + tolFt;
            double maxZ = bbox.Max.Z + tolFt;

            if (!IsFinite(minX) || !IsFinite(minY) || !IsFinite(minZ) ||
                !IsFinite(maxX) || !IsFinite(maxY) || !IsFinite(maxZ))
            {
                return null;
            }

            return new Outline(new XYZ(minX, minY, minZ), new XYZ(maxX, maxY, maxZ));
        }

        private static bool OutlinesIntersect(Outline a, Outline b)
        {
            XYZ aMin = a.MinimumPoint;
            XYZ aMax = a.MaximumPoint;
            XYZ bMin = b.MinimumPoint;
            XYZ bMax = b.MaximumPoint;

            bool x = aMin.X <= bMax.X && aMax.X >= bMin.X;
            bool y = aMin.Y <= bMax.Y && aMax.Y >= bMin.Y;
            bool z = aMin.Z <= bMax.Z && aMax.Z >= bMin.Z;

            return x && y && z;
        }

        private static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        private static bool StringEqualsIgnoreCase(string a, string b)
        {
            return string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeString(Func<string> getter)
        {
            try
            {
                return getter() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
