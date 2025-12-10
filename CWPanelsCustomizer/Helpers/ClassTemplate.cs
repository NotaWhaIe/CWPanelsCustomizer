//using System;
//using Autodesk.Revit.Attributes;
//using Autodesk.Revit.DB;
//using Autodesk.Revit.UI;

//namespace CWPanelsCustomizer.Helpers
//{
//    [Transaction(TransactionMode.Manual)]
//    public class ClassTemplate : IExternalCommand
//    {
//        public static string IS_NAME => "*Название плагина";
//        public static string IS_DESCRIPTION => "*Что делает плагин?";


//        public static string IS_TAB_NAME => "#BIM";
//        public static string IS_IMAGE => "CWPanelsCustomizer.Images.a1.png";

//        private SphereByPoint _sphereByPoint;
//        private UIDocument _uidoc;
//        private Document _doc;

//        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
//        {
//            _uidoc = commandData.Application.ActiveUIDocument;
//            _doc = _uidoc.Document;
//            _sphereByPoint = new SphereByPoint(_doc);

//            using (TransactionGroup tg = new TransactionGroup(_doc, IS_NAME))
//            {
//                tg.Start();

//                Method();

//                tg.Assimilate();
//            }

//            return Result.Succeeded;
//        }

//        private void Method()
//        {
//        }
//    }
//}
