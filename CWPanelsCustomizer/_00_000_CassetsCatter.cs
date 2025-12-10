using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CWPanelsCustomizer.Helpers;

namespace CWPanelsCustomizer
{
    [Transaction(TransactionMode.Manual)]
    public class _00_000_CassetsCatter : IExternalCommand
    {
        // Метаданные команды
        public static string IS_TAB_NAME => "#BIM";
        public static string IS_NAME => "!!Настройщик кассет";
        public static string IS_IMAGE => "CWPanelsCustomizer.Images.a1.png";
        public static string IS_DESCRIPTION => "Настраивает кассету по окну";

        private SphereByPoint _sphereByPoint;
        private UIDocument _uidoc;
        private Document _doc;

        private const double FEET_TO_MM = 304.8;
        private const double TOLERANCE = 0.0;

        // Вычисленные из рядовых для угловых панелей
        private double RightWidth { get; set; } = 0;
        private double LeftWidth { get; set; } = 0;
        private double TopHeight { get; set; } = 0;
        private double BottomHeight { get; set; } = 0;

        // Точка входа команды
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            _uidoc = commandData.Application.ActiveUIDocument;
            _doc = _uidoc.Document;
            _sphereByPoint = new SphereByPoint(_doc);

            // Группируем все транзакции в одну
            using (TransactionGroup tg = new TransactionGroup(_doc, IS_NAME))
            {
                tg.Start();

                TaskDialog.Show("test", "test");

                ResetPanels(); // Сбрасывает подрезки рядовых панелей в 0
                ReplacePanelsWithСutoutPanels(); // Замена рядовых панелей на угловые в углах окна 
                CalculateAndSetPanelCutout();   // Считает Вырез_Ширина/Высота на угловых панелях  
                CalculateAndSetPanels();    // Настраивает рядовые панели по угловым

                tg.Assimilate();
            }

            return Result.Succeeded;
        }

        // Рассчитывает ширины/высоты по рядовым панелям и заполняет свойства класса
        // Настройка рядовых панелей по окнам


        // Получение экземпляров семейства по имени
        private List<FamilyInstance> GetElementsByFamilyName(BuiltInCategory category, string familyNameContains, string symbolNameContains = null) =>
            new FilteredElementCollector(_doc)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Where(f => f.Symbol?.Family != null &&
                            f.Symbol.Family.Name.Contains(familyNameContains) &&
                           (symbolNameContains == null || f.Symbol.Name.Contains(symbolNameContains)))
                .ToList();

        // Все витражные стены
        private List<Wall> GetCurtainWalls() =>
            new FilteredElementCollector(_doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .Where(w => w?.CurtainGrid != null)
                .ToList();

        // Разделение панелей на рядовые и с вырезом по имени семейства
        private void GetPanelsByType(List<Wall> curtainWalls, out List<FamilyInstance> regularPanels, out List<FamilyInstance> cutPanels)
        {
            regularPanels = new List<FamilyInstance>();
            cutPanels = new List<FamilyInstance>();

            var allPanels = curtainWalls
                .SelectMany(w => w.CurtainGrid.GetPanelIds()
                    .Select(id => _doc.GetElement(id) as FamilyInstance)
                    .Where(p => p?.Symbol?.Family != null))
                .ToList();

            foreach (var panel in allPanels)
            {
                string name = panel.Symbol.Family.Name;
                if (name.Contains("КРСТ_НВФ_Рядовая_В3"))
                    regularPanels.Add(panel);
                else if (name.Contains("КРСТ_НВФ_С Г-образным вырезом_В2") || name.Contains("КРСТ_НВФ_С L-образным вырезом"))
                    cutPanels.Add(panel);
            }
        }

        // Установка параметра экземпляра
        private void SetParameterValue(FamilyInstance element, string paramName, double value)
        {
            var param = element.LookupParameter(paramName);
            if (param != null && !param.IsReadOnly)
                param.Set(value);
        }

        // Обнуление подрезок у рядовых панелей
        private void InitializeRegularPanelParameters(List<FamilyInstance> regularPanels)
        {
            foreach (var panel in regularPanels)
            {
                SetParameterValue(panel, "Подрезка", 0.0);
                SetParameterValue(panel, "Подрезка_Верх", 0.0);
                SetParameterValue(panel, "Подрезка_Низ", 0.0);
            }
        }

        // Пересечение двух bounding-box'ов
        private bool BoundingBoxesIntersect(BoundingBoxXYZ bbox1, BoundingBoxXYZ bbox2) =>
            !(bbox1.Max.X < bbox2.Min.X - TOLERANCE || bbox1.Min.X > bbox2.Max.X + TOLERANCE ||
              bbox1.Max.Y < bbox2.Min.Y - TOLERANCE || bbox1.Min.Y > bbox2.Max.Y + TOLERANCE ||
              bbox1.Max.Z < bbox2.Min.Z - TOLERANCE || bbox1.Min.Z > bbox2.Max.Z + TOLERANCE);

        // Центр bounding-box
        private XYZ GetCenter(BoundingBoxXYZ bbox) =>
            new XYZ((bbox.Min.X + bbox.Max.X) / 2.0, (bbox.Min.Y + bbox.Max.Y) / 2.0, (bbox.Min.Z + bbox.Max.Z) / 2.0);

        // Определение расположения панели относительно окна
        private string DeterminePanelPosition(BoundingBoxXYZ windowBBox, BoundingBoxXYZ panelBBox)
        {
            double windowCenterX = (windowBBox.Min.X + windowBBox.Max.X) / 2;
            double windowCenterY = (windowBBox.Min.Y + windowBBox.Max.Y) / 2;
            double windowCenterZ = (windowBBox.Min.Z + windowBBox.Max.Z) / 2;

            double panelCenterX = (panelBBox.Min.X + panelBBox.Max.X) / 2;
            double panelCenterY = (panelBBox.Min.Y + panelBBox.Max.Y) / 2;
            double panelCenterZ = (panelBBox.Min.Z + panelBBox.Max.Z) / 2;

            // Пересечения по осям
            double horizOverlapX = Math.Max(0, Math.Min(windowBBox.Max.X, panelBBox.Max.X) - Math.Max(windowBBox.Min.X, panelBBox.Min.X));
            double vertOverlap = Math.Max(0, Math.Min(windowBBox.Max.Z, panelBBox.Max.Z) - Math.Max(windowBBox.Min.Z, panelBBox.Min.Z));

            double deltaZ = Math.Abs(panelCenterZ - windowCenterZ);
            double deltaX = Math.Abs(panelCenterX - windowCenterX);
            double deltaY = Math.Abs(panelCenterY - windowCenterY);

            // Сверху/снизу
            if (deltaZ >= deltaX && deltaZ >= deltaY)
            {
                return panelCenterZ > windowCenterZ ? "Сверху" : "Снизу";
            }
            // Слева/справа
            else if (deltaX >= deltaY)
            {
                return panelCenterX < windowCenterX ? "Справа" : "Слева";
            }
            // Впереди/позади (редкий случай)
            else
            {
                return panelCenterY < windowCenterY ? "Позади" : "Впереди";
            }
        }

        // Пересечение BBox окна и панели → ширина/высота выреза
        private bool TryGetBBoxIntersectionSize(BoundingBoxXYZ windowBBox, BoundingBoxXYZ panelBBox,
            out double horizontal, out double vertical)
        {
            horizontal = vertical = 0;
            double minX = Math.Max(windowBBox.Min.X, panelBBox.Min.X);
            double maxX = Math.Min(windowBBox.Max.X, panelBBox.Max.X);
            double minZ = Math.Max(windowBBox.Min.Z, panelBBox.Min.Z);
            double maxZ = Math.Min(windowBBox.Max.Z, panelBBox.Max.Z);

            if (minX >= maxX || minZ >= maxZ)
                return false;

            horizontal = maxX - minX; // ширина выреза
            vertical = maxZ - minZ;   // высота выреза
            return true;
        }

        // Применение подрезок к рядовым панелям на основе ориентации
        private void ProcessRegularPanels(List<FamilyInstance> windows, List<FamilyInstance> regularPanels)
        {
            int totalProcessed = 0, updated = 0;
            string resultMessage = "";

            using (var trans = new Transaction(_doc, "Установка параметров Подрезка рядовых"))
            {
                trans.Start();

                foreach (var window in windows)
                {
                    var windowBBox = window.get_BoundingBox(null);
                    if (windowBBox == null)
                        continue;

                    foreach (var panel in regularPanels)
                    {
                        var panelBBox = panel.get_BoundingBox(null);
                        if (panelBBox == null || !BoundingBoxesIntersect(windowBBox, panelBBox))
                            continue;

                        if (!TryGetBBoxIntersectionSize(windowBBox, panelBBox, out double horizontal, out double vertical))
                            continue;

                        totalProcessed++;

                        // Определяем ориентацию панели относительно окна
                        string position = DeterminePanelPosition(windowBBox, panelBBox);

                        string paramName = "";
                        double value = 0;

                        // Логика записи параметров в зависимости от ориентации
                        switch (position)
                        {
                            case "Справа":
                                paramName = "Подрезка";
                                value = LeftWidth;
                                break;
                            case "Слева":
                                paramName = "Подрезка";
                                value = RightWidth;
                                break;
                            case "Сверху":
                                paramName = "Подрезка_Низ";
                                value = TopHeight;
                                break;
                            case "Снизу":
                                paramName = "Подрезка_Верх";
                                value = BottomHeight;
                                break;
                        }

                        if (!string.IsNullOrEmpty(paramName) && value > 0)
                        {
                            SetParameterValue(panel, paramName, value);
                            resultMessage += $"✓ Панель {panel.Id.IntegerValue} ({position}): {paramName}={value * FEET_TO_MM:F2} мм\n";
                            updated++;
                        }
                    }
                }

                trans.Commit();
            }

            //TaskDialog.Show("Результат",
            //    $"Обработано рядовых панелей: {totalProcessed}\nОбновлено параметров: {updated}\n\n{resultMessage}");
        }

        // DTO для хранения расстояний выреза
        private class PanelDistances
        {
            public double HorizontalDistance { get; set; } // ширина выреза
            public double VerticalDistance { get; set; }   // высота выреза
        }

        // Настройка угловых панелей: записываем Вырез_Ширина/Высота по пересечению BBo
        private void CalculateAndSetPanelCutout()
        {
            try
            {
                var genericModels = GetElementsByFamilyName(
                    BuiltInCategory.OST_GenericModel,
                    "#_Оконный проем_Прямоугольный",
                    "Без бруса");

                var curtainWalls = GetCurtainWalls();

                // Собираем только угловые панели с вырезом
                var cornerPanels = curtainWalls
                    .SelectMany(w => w.CurtainGrid.GetPanelIds()
                        .Select(id => _doc.GetElement(id) as FamilyInstance)
                        .Where(p => p?.Symbol?.Family != null &&
                                  (p.Symbol.Family.Name.Contains("КРСТ_НВФ_С Г-образным вырезом_В2") ||
                                   p.Symbol.Family.Name.Contains("КРСТ_НВФ_С L-образным вырезом"))))
                    .ToList();

                if (!genericModels.Any() || !cornerPanels.Any())
                    return;

                // Берём первое окно (если их несколько, можно позже расширить логику)
                var window = genericModels.First();
                var windowBBox = window.get_BoundingBox(null);
                if (windowBBox == null)
                    return;

                // Центр окна – используем для определения положения панелей
                var windowCenter = GetCenter(windowBBox);

                // Переменные для 4 углов: левый/правый + верх/низ
                double leftTopWidth = 0, leftTopHeight = 0;
                double leftBottomWidth = 0, leftBottomHeight = 0;
                double rightTopWidth = 0, rightTopHeight = 0;
                double rightBottomWidth = 0, rightBottomHeight = 0;

                // Храним также ссылки на панели, чтобы потом записать усреднённые/исправленные значения
                FamilyInstance leftTopPanel = null;
                FamilyInstance leftBottomPanel = null;
                FamilyInstance rightTopPanel = null;
                FamilyInstance rightBottomPanel = null;

                // Сначала для каждой угловой панели считаем ширину/высоту по пересечению BBox с окном и раскладываем по углам
                foreach (var panel in cornerPanels)
                {
                    var panelBBox = panel.get_BoundingBox(null);
                    if (panelBBox == null || !BoundingBoxesIntersect(windowBBox, panelBBox))
                        continue;

                    if (!TryGetBBoxIntersectionSize(windowBBox, panelBBox, out double width, out double height))
                        continue;

                    // Определяем положение панели относительно окна:
                    var panelCenter = GetCenter(panelBBox);
                    bool isLeft = panelCenter.X < windowCenter.X;
                    bool isTop = panelCenter.Z > windowCenter.Z;

                    if (isLeft && isTop)
                    {
                        leftTopWidth = width;
                        leftTopHeight = height;
                        leftTopPanel = panel;

                    }
                    else if (isLeft && !isTop)
                    {
                        leftBottomWidth = width;
                        leftBottomHeight = height;
                        leftBottomPanel = panel;

                    }
                    else if (!isLeft && isTop)
                    {
                        rightTopWidth = width;
                        rightTopHeight = height;
                        rightTopPanel = panel;

                    }
                    else // !isLeft && !isTop
                    {
                        rightBottomWidth = width;
                        rightBottomHeight = height;
                        rightBottomPanel = panel;

                    }
                }
                double leftWidth = Math.Abs(leftBottomWidth - leftTopWidth) / 2 + Math.Min(leftBottomWidth, leftTopWidth);
                double rightWidth = Math.Abs(rightBottomWidth - rightTopWidth) / 2 + Math.Min(rightBottomWidth, rightTopWidth);

                LeftWidth = leftWidth;
                RightWidth = rightWidth;
                BottomHeight = rightBottomHeight;
                TopHeight = leftTopHeight;

                // Если панель в углу отсутствует – значения остаются 0 (как и требуется)

                string resultMessage = "";
                int processed = 0, updated = 0;

                using (var transGroup = new TransactionGroup(_doc, "Обработка панелей с вырезом"))
                using (var trans = new Transaction(_doc, "Установка параметров вырезов по углам"))
                {
                    transGroup.Start();
                    trans.Start();

                    // Левая верхняя
                    if (leftTopPanel != null && (leftTopWidth > 0 || leftTopHeight > 0))
                    {
                        SetParameterValue(leftTopPanel, "Вырез_Ширина", leftWidth);
                        SetParameterValue(leftTopPanel, "Вырез_Высота", leftTopHeight);
                        resultMessage += $"✓ Левая верхняя панель {leftTopPanel.Id.IntegerValue}: Ширина={leftTopWidth * FEET_TO_MM:F2}, Высота={leftTopHeight * FEET_TO_MM:F2} мм\n";
                        processed++;
                        updated += 2;
                    }

                    // Левая нижняя
                    if (leftBottomPanel != null && (leftBottomWidth > 0 || leftBottomHeight > 0))
                    {
                        SetParameterValue(leftBottomPanel, "Вырез_Ширина", leftWidth);
                        SetParameterValue(leftBottomPanel, "Вырез_Высота", leftBottomHeight);
                        resultMessage += $"✓ Левая нижняя панель {leftBottomPanel.Id.IntegerValue}: Ширина={leftBottomWidth * FEET_TO_MM:F2}, Высота={leftBottomHeight * FEET_TO_MM:F2} мм\n";
                        processed++;
                        updated += 2;
                    }

                    // Правая верхняя
                    if (rightTopPanel != null && (rightTopWidth > 0 || rightTopHeight > 0))
                    {
                        SetParameterValue(rightTopPanel, "Вырез_Высота", rightTopHeight);
                        SetParameterValue(rightTopPanel, "Вырез_Ширина", rightWidth);
                        resultMessage += $"✓ Правая верхняя панель {rightTopPanel.Id.IntegerValue}: Ширина={rightTopWidth * FEET_TO_MM:F2}, Высота={rightTopHeight * FEET_TO_MM:F2} мм\n";
                        processed++;
                        updated += 2;
                    }

                    // Правая нижняя
                    if (rightBottomPanel != null && (rightBottomWidth > 0 || rightBottomHeight > 0))
                    {
                        SetParameterValue(rightBottomPanel, "Вырез_Высота", rightBottomHeight);
                        SetParameterValue(rightBottomPanel, "Вырез_Ширина", rightWidth);
                        resultMessage += $"✓ Правая нижняя панель {rightBottomPanel.Id.IntegerValue}: Ширина={rightBottomWidth * FEET_TO_MM:F2}, Высота={rightBottomHeight * FEET_TO_MM:F2} мм\n";
                        processed++;
                        updated += 2;
                    }

                    trans.Commit();
                    transGroup.Assimilate();
                }
                //    $"Обработано угловых панелей: {processed}\nОбновлено параметров: {updated}\n\n{resultMessage}");
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Ошибка CassetsCatter_1", ex.Message);
            }
        }

        // Сброс подрезок у рядовых панелей в 0
        private void ResetPanels()
        {
            try
            {
                var curtainWalls = GetCurtainWalls();
                var regularPanels = curtainWalls
                    .SelectMany(w => w.CurtainGrid.GetPanelIds()
                        .Select(id => _doc.GetElement(id) as FamilyInstance)
                        .Where(p => p?.Symbol?.Family?.Name.Contains("КРСТ_НВФ_Рядовая_В3") == true))
                    .ToList();

                if (regularPanels.Count == 0)
                    return;

                using (var trans = new Transaction(_doc, "Инициализация параметров Подрезка"))
                {
                    trans.Start();
                    InitializeRegularPanelParameters(regularPanels);
                    trans.Commit();
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Ошибка инициализации", ex.Message);
            }
        }

        // Настройка рядовых панелей по угловым
        private void CalculateAndSetPanels()
        {
            try
            {
                var windows = GetElementsByFamilyName(
                    BuiltInCategory.OST_GenericModel,
                    "#_Оконный проем_Прямоугольный",
                    "Без бруса");

                var curtainWalls = GetCurtainWalls();
                GetPanelsByType(curtainWalls, out var regularPanels, out var cutPanels);

                ProcessRegularPanels(windows, regularPanels);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Ошибка CassetsCatter_2", ex.Message);
            }
        }

        // Замена рядовых панелей на угловые в углах окна 
        private void ReplacePanelsWithСutoutPanels()
        {
            try
            {
                // ШАГ 1: ИНИЦИАЛИЗАЦИЯ И СБОР ГЕОМЕТРИИ
                var windows = GetElementsByFamilyName(
                    BuiltInCategory.OST_GenericModel,
                    "#_Оконный проем_Прямоугольный",
                    "Без бруса");

                if (!windows.Any())
                {
                    TaskDialog.Show("Результат", "Окна не найдены");
                    return;
                }

                var curtainWalls = GetCurtainWalls();
                var allPanels = curtainWalls
                    .SelectMany(w => w.CurtainGrid.GetPanelIds()
                        .Select(id => _doc.GetElement(id) as FamilyInstance)
                        .Where(p => p?.Symbol?.Family != null))
                    .ToList();

                const double CHECK_SEGMENT_LENGTH = 0.328084; // 100 мм в футах
                const double PANEL_BBOX_REDUCTION_FACTOR = 0.7;

                string resultMessage = "";
                int replaced = 0;

                using (var trans = new Transaction(_doc, "Замена рядовых панелей на угловые"))
                {
                    trans.Start();

                    // ШАГ 2: ОБРАБОТКА КАЖДОГО ОКНА И ВЕРИФИКАЦИЯ УГЛОВ
                    foreach (var window in windows)
                    {
                        var windowBBox = window.get_BoundingBox(null);
                        if (windowBBox == null)
                            continue;

                        var windowCenter = GetCenter(windowBBox);

                        // 2.1 Определение продольной плоскости витража (плоскость XZ)
                        // Предполагаем, что все витражные элементы находятся на одной Y-плоскости
                        double curtainWallPlaneY = windowCenter.Y;

                        // Угловые точки окна (в 3D пространстве)
                        var windowCornerTL = new XYZ(windowBBox.Min.X, windowBBox.Min.Y, windowBBox.Max.Z);
                        var windowCornerTR = new XYZ(windowBBox.Max.X, windowBBox.Min.Y, windowBBox.Max.Z);
                        var windowCornerBL = new XYZ(windowBBox.Min.X, windowBBox.Min.Y, windowBBox.Min.Z);
                        var windowCornerBR = new XYZ(windowBBox.Max.X, windowBBox.Min.Y, windowBBox.Min.Z);

                        // Проецируем угловые точки окна на плоскость витража (меняем Y-координату на плоскость витража)
                        var projWindowCornerTL = new XYZ(windowCornerTL.X, curtainWallPlaneY, windowCornerTL.Z);
                        var projWindowCornerTR = new XYZ(windowCornerTR.X, curtainWallPlaneY, windowCornerTR.Z);
                        var projWindowCornerBL = new XYZ(windowCornerBL.X, curtainWallPlaneY, windowCornerBL.Z);
                        var projWindowCornerBR = new XYZ(windowCornerBR.X, curtainWallPlaneY, windowCornerBR.Z);

                        // 2.2 Фильтрация потенциальных панелей
                        var candidatePanels = allPanels
                            .Where(p => p.Symbol.Family.Name == "КРСТ_НВФ_Рядовая_В3")
                            .Where(p =>
                            {
                                var pBBox = p.get_BoundingBox(null);
                                if (pBBox == null)
                                    return false;

                                var reducedPanelBBox = ReduceBoundingBox(pBBox, PANEL_BBOX_REDUCTION_FACTOR);
                                return BoundingBoxesIntersect(windowBBox, reducedPanelBBox);
                            })
                            .ToList();

                        if (candidatePanels.Count == 0)
                            continue;

                        // 2.3 Верификация угловых панелей с использованием проецирования на плоскость витража
                        var panelsToReplace = new HashSet<FamilyInstance>();

                        // Определяем углы и их направления (в плоскости XZ)
                        var corners = new List<(XYZ projCorner, XYZ dirVertical, XYZ dirHorizontal, string cornerName)>
                {
                    (projWindowCornerTL, new XYZ(0, 0, 1), new XYZ(-1, 0, 0), "TL"),   // Левый верхний: вверх и влево
                    (projWindowCornerTR, new XYZ(0, 0, 1), new XYZ(1, 0, 0), "TR"),    // Правый верхний: вверх и вправо
                    (projWindowCornerBL, new XYZ(0, 0, -1), new XYZ(-1, 0, 0), "BL"),  // Левый нижний: вниз и влево
                    (projWindowCornerBR, new XYZ(0, 0, -1), new XYZ(1, 0, 0), "BR")    // Правый нижний: вниз и вправо
                };

                        // 2.3.1 Проведение проецированных линий и проверка пересечений
                        foreach (var corner in corners)
                        {
                            // Создаём две проецированные линии на плоскость витража XZ
                            var p1Vertical = corner.projCorner;
                            var p2Vertical = corner.projCorner + corner.dirVertical * CHECK_SEGMENT_LENGTH;

                            var p1Horizontal = corner.projCorner;
                            var p2Horizontal = corner.projCorner + corner.dirHorizontal * CHECK_SEGMENT_LENGTH;

                            // 2.3.2 Определение пересечений на проецированной плоскости
                            var hitPanelsVertical = GetIntersectingPanelsOnPlane(candidatePanels, p1Vertical, p2Vertical, curtainWallPlaneY);
                            var hitPanelsHorizontal = GetIntersectingPanelsOnPlane(candidatePanels, p1Horizontal, p2Horizontal, curtainWallPlaneY);

                            // 2.3.3 Логика замены
                            var commonPanels = hitPanelsVertical.Intersect(hitPanelsHorizontal).ToList();

                            if (commonPanels.Count > 0)
                            {
                                foreach (var panel in commonPanels)
                                {
                                    panelsToReplace.Add(panel);
                                    resultMessage += $"ℹ Угол {corner.cornerName}: панель {panel.Id.IntegerValue} пересекает обе стороны угла\n";
                                }
                            }
                        }

                        // ШАГ 3: ОПРЕДЕЛЕНИЕ ТИПА ЗАМЕНЫ И ВЫПОЛНЕНИЕ ДЕЙСТВИЯ
                        foreach (var panelTarget in panelsToReplace)
                        {
                            var panelBBox = panelTarget.get_BoundingBox(null);
                            if (panelBBox == null)
                                continue;

                            var panelCenter = GetCenter(panelBBox);

                            // 3.1 Определение относительного положения
                            bool isTop = panelCenter.Z > windowCenter.Z;

                            // 3.2 Выполнение замены
                            string targetFamilyName = isTop
                                ? "КРСТ_НВФ_С Г-образным вырезом_В2"
                                : "КРСТ_НВФ_С L-образным вырезом";

                            var targetSymbol = GetFamilySymbolByName(targetFamilyName);
                            if (targetSymbol == null)
                            {
                                resultMessage += $"✗ Панель {panelTarget.Id.IntegerValue}: не найдено целевое семейство {targetFamilyName}\n";
                                continue;
                            }

                            try
                            {
                                if (!targetSymbol.IsActive)
                                    targetSymbol.Activate();

                                panelTarget.Symbol = targetSymbol;

                                resultMessage += $"✓ Панель {panelTarget.Id.IntegerValue}: заменена на {targetFamilyName}\n";
                                replaced++;
                            }
                            catch (Exception ex)
                            {
                                resultMessage += $"✗ Панель {panelTarget.Id.IntegerValue}: {ex.Message}\n";
                            }
                        }
                    }

                    trans.Commit();
                }

                // Вывод результатов
                if (replaced > 0)
                {
                    TaskDialog.Show("Результат замены панелей",
                        $"Заменено панелей: {replaced}\n\n{resultMessage}");
                }
                else
                {
                    TaskDialog.Show("Результат", "Панели в углах окна не найдены или установлены корректно");
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Ошибка CassetsCatter_0", ex.Message + "\n\n" + ex.StackTrace);
            }
        }

        // Находит панели, которые пересекаются с линией на плоскости витража (XZ плоскость)
        private List<FamilyInstance> GetIntersectingPanelsOnPlane(List<FamilyInstance> panels, XYZ p1, XYZ p2, double planeY)
        {
            var intersectingPanels = new List<FamilyInstance>();

            foreach (var panel in panels)
            {
                var panelBBox = panel.get_BoundingBox(null);
                if (panelBBox == null)
                    continue;

                // Проецируем BBox панели на плоскость витража
                var projPanelMin = new XYZ(panelBBox.Min.X, planeY, panelBBox.Min.Z);
                var projPanelMax = new XYZ(panelBBox.Max.X, planeY, panelBBox.Max.Z);

                // Проверяем пересечение линии с проецированным прямоугольником панели в плоскости XZ
                if (LineIntersectsProjectedBBox(p1, p2, projPanelMin, projPanelMax))
                {
                    intersectingPanels.Add(panel);
                }
            }

            return intersectingPanels;
        }

        // Проверяет, пересекает ли линия проецированный прямоугольник в плоскости XZ
        private bool LineIntersectsProjectedBBox(XYZ p1, XYZ p2, XYZ projMin, XYZ projMax)
        {
            // Проверяем, находится ли хотя бы один конец линии внутри проецированного прямоугольника
            if (IsPointInProjectedBBox(p1, projMin, projMax) || IsPointInProjectedBBox(p2, projMin, projMax))
                return true;

            // Проверяем пересечение линии с границами прямоугольника в плоскости XZ
            // Граница слева (x = projMin.X)
            if (LineIntersectsVerticalLine(p1, p2, projMin.X, projMin.Z, projMax.Z))
                return true;

            // Граница справа (x = projMax.X)
            if (LineIntersectsVerticalLine(p1, p2, projMax.X, projMin.Z, projMax.Z))
                return true;

            // Граница снизу (z = projMin.Z)
            if (LineIntersectsHorizontalLine(p1, p2, projMin.Z, projMin.X, projMax.X))
                return true;

            // Граница сверху (z = projMax.Z)
            if (LineIntersectsHorizontalLine(p1, p2, projMax.Z, projMin.X, projMax.X))
                return true;

            return false;
        }

        // Проверяет, находится ли точка внутри проецированного прямоугольника в плоскости XZ
        private bool IsPointInProjectedBBox(XYZ point, XYZ projMin, XYZ projMax)
        {
            const double TOLERANCE = 0.001;
            return point.X >= projMin.X - TOLERANCE && point.X <= projMax.X + TOLERANCE &&
                   point.Z >= projMin.Z - TOLERANCE && point.Z <= projMax.Z + TOLERANCE;
        }

        // Проверяет пересечение линии с вертикальной линией прямоугольника (x = constant, z изменяется)
        private bool LineIntersectsVerticalLine(XYZ p1, XYZ p2, double lineX, double minZ, double maxZ)
        {
            if (Math.Abs(p2.X - p1.X) < 0.0001)
                return false; // Линия вертикальна в плоскости XZ

            // Вычисляем параметр t для пересечения по X
            double t = (lineX - p1.X) / (p2.X - p1.X);

            if (t >= 0 && t <= 1)
            {
                // Вычисляем Z координату в точке пересечения
                double intersectZ = p1.Z + (p2.Z - p1.Z) * t;
                return intersectZ >= minZ - 0.001 && intersectZ <= maxZ + 0.001;
            }

            return false;
        }

        // Проверяет пересечение линии с горизонтальной линией прямоугольника (z = constant, x изменяется)
        private bool LineIntersectsHorizontalLine(XYZ p1, XYZ p2, double lineZ, double minX, double maxX)
        {
            if (Math.Abs(p2.Z - p1.Z) < 0.0001)
                return false; // Линия горизонтальна в плоскости XZ

            // Вычисляем параметр t для пересечения по Z
            double t = (lineZ - p1.Z) / (p2.Z - p1.Z);

            if (t >= 0 && t <= 1)
            {
                // Вычисляем X координату в точке пересечения
                double intersectX = p1.X + (p2.X - p1.X) * t;
                return intersectX >= minX - 0.001 && intersectX <= maxX + 0.001;
            }

            return false;
        }

        // Уменьшает размер BoundingBox на заданный коэффициент от его центра
        private BoundingBoxXYZ ReduceBoundingBox(BoundingBoxXYZ bbox, double reductionFactor)
        {
            var center = GetCenter(bbox);

            double halfLengthX = (bbox.Max.X - bbox.Min.X) / 2.0;
            double halfLengthY = (bbox.Max.Y - bbox.Min.Y) / 2.0;
            double halfLengthZ = (bbox.Max.Z - bbox.Min.Z) / 2.0;

            double newHalfLengthX = halfLengthX * reductionFactor;
            double newHalfLengthY = halfLengthY * reductionFactor;
            double newHalfLengthZ = halfLengthZ * reductionFactor;

            var reducedMin = new XYZ(
                center.X - newHalfLengthX,
                center.Y - newHalfLengthY,
                center.Z - newHalfLengthZ);

            var reducedMax = new XYZ(
                center.X + newHalfLengthX,
                center.Y + newHalfLengthY,
                center.Z + newHalfLengthZ);

            return new BoundingBoxXYZ { Min = reducedMin, Max = reducedMax };
        }

        // Получение символа семейства по имени
        private FamilySymbol GetFamilySymbolByName(string familyName)
        {
            try
            {
                var family = new FilteredElementCollector(_doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .FirstOrDefault(f => f.Name == familyName);

                if (family == null)
                    return null;

                var symbolIds = family.GetFamilySymbolIds();
                if (symbolIds.Count == 0)
                    return null;

                var firstSymbolId = symbolIds.First();
                return _doc.GetElement(firstSymbolId) as FamilySymbol;
            }
            catch
            {
                return null;
            }
        }
    }
}
