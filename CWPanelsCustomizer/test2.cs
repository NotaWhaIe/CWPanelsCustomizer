using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using CWPanelsCustomizer.Helpers;

namespace CWPanelsCustomizer
{
    [Transaction(TransactionMode.Manual)]
    public class test2 : IExternalCommand
    {
        public static string IS_NAME => "Размещение стоек";
        public static string IS_DESCRIPTION => "Размещение семейства стоек по вертикальным линиям витража";
        public static string IS_TAB_NAME => "#BIM";
        public static string IS_IMAGE => "CWPanelsCustomizer.Images.a1.png";

        private SphereByPoint _sphereByPoint;
        private UIDocument _uidoc;
        private Document _doc;

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
            Debug.WriteLine("=== НАЧАЛО РАЗМЕЩЕНИЯ СТОЕК ===");

            List<Wall> curtainWalls = GetCurtainWalls();
            Debug.WriteLine($"Найдено витражей: {curtainWalls.Count}");

            FamilySymbol familySymbol = GetFamilySymbol("КРСТ_НВФ_ZIAS_Массив стоек с кронштейнами_В2", "187");

            Debug.WriteLine($"Семейство найдено: {(familySymbol != null ? "ДА" : "НЕТ")}");

            if (familySymbol == null)
            {
                Debug.WriteLine("ОШИБКА: Семейство не найдено");
                TaskDialog.Show("Ошибка", "Семейство не найдено. Проверь имя семейства и имя типа.");
                return;
            }

            int totalPosts = 0;
            foreach (Wall wall in curtainWalls)
            {
                int postsPlaced = PlacePostsAlongVerticalGridLines(wall, familySymbol);
                totalPosts += postsPlaced;
                Debug.WriteLine($"Витраж '{wall.Name}' (ID: {wall.Id.IntegerValue}): размещено стоек = {postsPlaced}");
            }

            Debug.WriteLine($"=== ИТОГО размещено стоек: {totalPosts} ===");
        }

        private List<Wall> GetCurtainWalls()
        {
            int allWallsCount = new FilteredElementCollector(_doc)
                .OfClass(typeof(Wall))
                .Count();
            Debug.WriteLine($"Всего стен в проекте: {allWallsCount}");

            List<Wall> walls = new FilteredElementCollector(_doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .Where(w => w != null && w.CurtainGrid != null)
                .ToList();

            Debug.WriteLine($"Из них витражей (с CurtainGrid): {walls.Count}");

            return walls;
        }

        private FamilySymbol GetFamilySymbol(string familyName, string typeName)
        {
            Debug.WriteLine($"Поиск семейства: '{familyName}', тип: '{typeName}'");

            FamilySymbol symbol = new FilteredElementCollector(_doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(fs =>
                    fs.Family != null &&
                    fs.Family.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase) &&
                    fs.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));

            if (symbol == null)
            {
                Debug.WriteLine("Символ не найден обычным фильтром. Выведем все подходящие по имени семейства:");

                IEnumerable<FamilySymbol> allSymbolsSameFamilyName = new FilteredElementCollector(_doc)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .Where(fs => fs.Family != null &&
                                 fs.Family.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));

                foreach (FamilySymbol fs in allSymbolsSameFamilyName)
                {
                    string catName = fs.Family.FamilyCategory != null
                        ? fs.Family.FamilyCategory.Name
                        : "<no category>";
                    Debug.WriteLine($"  Найден тип: Id={fs.Id.IntegerValue}, Name='{fs.Name}', Category='{catName}'");
                }
            }
            else
            {
                string catName = symbol.Family.FamilyCategory != null
                    ? symbol.Family.FamilyCategory.Name
                    : "<no category>";
                Debug.WriteLine($"Найден символ: Id={symbol.Id.IntegerValue}, Name='{symbol.Name}', Category='{catName}'");
            }

            return symbol;
        }

        private int PlacePostsAlongVerticalGridLines(Wall curtainWall, FamilySymbol familySymbol)
        {
            Debug.WriteLine($"Обработка витража: {curtainWall.Name} (ID: {curtainWall.Id.IntegerValue})");

            CurtainGrid curtainGrid = curtainWall.CurtainGrid;
            if (curtainGrid == null)
            {
                Debug.WriteLine("  -> CurtainGrid = null");
                return 0;
            }

            ICollection<ElementId> verticalGridLineIds = curtainGrid.GetVGridLineIds();
            Debug.WriteLine($"  -> Вертикальных линий: {verticalGridLineIds.Count}");

            Face curtainWallFace = GetLargestFaceFromWall(curtainWall);
            if (curtainWallFace == null)
            {
                Debug.WriteLine("  -> ОШИБКА: Не удалось получить грань витража");
                return 0;
            }

            Debug.WriteLine("  -> Грань витража успешно получена");

            int postsPlaced = 0;

            foreach (ElementId gridLineId in verticalGridLineIds)
            {
                CurtainGridLine gridLine = _doc.GetElement(gridLineId) as CurtainGridLine;

                if (gridLine == null)
                {
                    Debug.WriteLine($"    -> GridLine {gridLineId.IntegerValue} = null");
                    continue;
                }

                CurveArray segmentCurves = gridLine.AllSegmentCurves;

                if (segmentCurves == null || segmentCurves.Size == 0)
                {
                    Debug.WriteLine($"    -> GridLine {gridLineId.IntegerValue}: сегментов = 0");
                    continue;
                }

                Curve segmentCurve = segmentCurves.get_Item(0);
                XYZ startPoint = segmentCurve.GetEndPoint(0);
                XYZ endPoint = segmentCurve.GetEndPoint(1);

                Debug.WriteLine(
                    $"    -> Линия: X={startPoint.X:F3}, Y={startPoint.Y:F3}, Z={startPoint.Z:F3}");

                try
                {
                    using (Transaction transaction = new Transaction(_doc, "Размещение стойки"))
                    {
                        transaction.Start();

                        if (!familySymbol.IsActive)
                        {
                            familySymbol.Activate();
                            Debug.WriteLine("    -> Семейство активировано");
                        }

                        Line hostLine = Line.CreateBound(startPoint, endPoint);
                        FamilyInstance instance = _doc.Create.NewFamilyInstance(
                            curtainWallFace,
                            hostLine,
                            familySymbol);

                        transaction.Commit();
                        postsPlaced++;
                        Debug.WriteLine($"    -> Стойка размещена на грани витража (ID: {instance.Id.IntegerValue})");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"    -> ОШИБКА размещения: {ex.Message}");
                }
            }

            return postsPlaced;
        }

        private Face GetLargestFaceFromWall(Wall wall)
        {
            try
            {
                Options options = new Options
                {
                    IncludeNonVisibleObjects = true,
                    ComputeReferences = true
                };
                GeometryElement geom = wall.get_Geometry(options);

                Face largestFace = null;
                double maxArea = 0;

                foreach (GeometryObject gObj in geom)
                {
                    if (gObj is Solid solid && solid.Faces.Size > 0)
                    {
                        foreach (Face face in solid.Faces)
                        {
                            try
                            {
                                double area = face.Area;
                                Debug.WriteLine($"  -> Найдена грань с площадью: {area:F2}");

                                if (area > maxArea)
                                {
                                    maxArea = area;
                                    largestFace = face;
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"  -> Ошибка при получении площади грани: {ex.Message}");
                            }
                        }
                    }

                    if (gObj is GeometryInstance geoInst)
                    {
                        GeometryElement instGeom = geoInst.GetInstanceGeometry();
                        foreach (GeometryObject instObj in instGeom)
                        {
                            if (instObj is Solid solid2 && solid2.Faces.Size > 0)
                            {
                                foreach (Face face in solid2.Faces)
                                {
                                    try
                                    {
                                        double area = face.Area;
                                        Debug.WriteLine($"  -> Найдена грань (из instance) с площадью: {area:F2}");

                                        if (area > maxArea)
                                        {
                                            maxArea = area;
                                            largestFace = face;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Debug.WriteLine($"  -> Ошибка при получении площади грани instance: {ex.Message}");
                                    }
                                }
                            }
                        }
                    }
                }

                if (largestFace != null)
                {
                    Debug.WriteLine($"  -> Выбрана крупнейшая грань площадью: {maxArea:F2}");
                }

                return largestFace;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ОШИБКА в GetLargestFaceFromWall: {ex.Message}");
            }

            return null;
        }
    }
}
