using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace RevitCommandBridge
{
    internal static class RevitPlanQueries
    {
        public static Dictionary<string, object> QueryDocument(PlanExecutionContext context)
        {
            Document document = context.Document;
            return new Dictionary<string, object>
            {
                { "title", document.Title },
                { "path", document.PathName ?? string.Empty },
                { "is_family_document", document.IsFamilyDocument },
                { "is_read_only", document.IsReadOnly },
                { "active_view", document.ActiveView == null ? null : document.ActiveView.Name },
                { "active_view_id", document.ActiveView == null ? (object)null : document.ActiveView.Id.IntegerValue },
                { "revit_api", BridgeBuildInfo.RevitVersion }
            };
        }

        public static Dictionary<string, object> QueryCatalog(PlanStep step, PlanExecutionContext context)
        {
            string kind = PlanValues.String(step.Arguments, "types", "kind").ToLowerInvariant();
            int limit = ReadLimit(step.Arguments);
            switch (kind)
            {
                case "levels":
                    return QueryLevels(context.Document, limit);
                case "categories":
                    return QueryCategories(context.Document, limit);
                case "views":
                    return QueryViews(context.Document, limit);
                case "families":
                    return QueryFamilies(context.Document, step.Arguments, limit);
                case "types":
                    return QueryTypes(context.Document, step.Arguments, limit, false);
                case "mep_types":
                    return QueryTypes(context.Document, step.Arguments, limit, true);
                default:
                    throw new BridgeCommandException("query_catalog.kind 仅支持 levels、categories、views、families、types、mep_types。");
            }
        }

        public static Dictionary<string, object> QueryElements(PlanStep step, PlanExecutionContext context)
        {
            Document document = context.Document;
            int limit = ReadLimit(step.Arguments);
            bool includeTypes = PlanValues.Boolean(step.Arguments, false, "include_types");
            string nameContains = PlanValues.String(step.Arguments, null, "name_contains", "name");
            string typeName = PlanValues.String(step.Arguments, null, "type_name", "type");
            string familyName = PlanValues.String(step.Arguments, null, "family", "family_name");
            List<string> parameterNames = ReadStringList(PlanValues.Get(step.Arguments, "parameters", "parameter_names"));
            object requestedIds = PlanValues.Get(step.Arguments, "element_ids", "ids");

            IEnumerable<Element> candidates;
            if (requestedIds != null)
            {
                IList<ElementId> ids = context.ResolveElementIds(step.Arguments, "element_ids", "ids");
                candidates = ids.Select(document.GetElement).Where(element => element != null);
            }
            else
            {
                FilteredElementCollector collector = new FilteredElementCollector(document);
                object category = PlanValues.Get(step.Arguments, "category", "category_id");
                if (category != null)
                {
                    collector.WherePasses(new ElementCategoryFilter(
                        RevitLookups.ResolveCategoryId(document, step.Arguments, BuiltInCategory.OST_GenericModel)));
                }
                if (!includeTypes)
                {
                    collector.WhereElementIsNotElementType();
                }
                candidates = collector.ToElements();
            }

            if (!string.IsNullOrWhiteSpace(nameContains))
            {
                candidates = candidates.Where(element => RevitLookups.ElementName(element)
                    .IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            if (!string.IsNullOrWhiteSpace(typeName))
            {
                candidates = candidates.Where(element => TypeName(document, element)
                    .IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            if (!string.IsNullOrWhiteSpace(familyName))
            {
                candidates = candidates.Where(element => FamilyName(document, element)
                    .IndexOf(familyName, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            List<Element> materialized = candidates.OrderBy(element => element.Id.IntegerValue).Take(limit + 1).ToList();
            bool truncated = materialized.Count > limit;
            if (truncated)
            {
                materialized.RemoveAt(materialized.Count - 1);
            }
            var elements = new List<Dictionary<string, object>>();
            foreach (Element element in materialized)
            {
                elements.Add(RevitLookups.ElementData(document, element, parameterNames));
            }
            return new Dictionary<string, object>
            {
                { "count", elements.Count },
                { "truncated", truncated },
                { "limit", limit },
                { "elements", elements }
            };
        }

        private static Dictionary<string, object> QueryLevels(Document document, int limit)
        {
            List<Level> levels = new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => level.Elevation)
                .Take(limit)
                .ToList();
            var items = new List<Dictionary<string, object>>();
            foreach (Level level in levels)
            {
                items.Add(new Dictionary<string, object>
                {
                    { "id", level.Id.IntegerValue },
                    { "name", level.Name },
                    { "elevation_mm", PlanValues.ToMillimeters(level.Elevation) }
                });
            }
            return new Dictionary<string, object> { { "kind", "levels" }, { "items", items } };
        }

        private static Dictionary<string, object> QueryCategories(Document document, int limit)
        {
            var items = new List<Dictionary<string, object>>();
            foreach (Category category in document.Settings.Categories.Cast<Category>().OrderBy(category => category.Name).Take(limit))
            {
                items.Add(new Dictionary<string, object>
                {
                    { "id", category.Id.IntegerValue },
                    { "name", category.Name },
                    { "category_type", category.CategoryType.ToString() }
                });
            }
            return new Dictionary<string, object> { { "kind", "categories" }, { "items", items } };
        }

        private static Dictionary<string, object> QueryViews(Document document, int limit)
        {
            var items = new List<Dictionary<string, object>>();
            foreach (View view in new FilteredElementCollector(document)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(view => !view.IsTemplate)
                .OrderBy(view => view.Name)
                .Take(limit))
            {
                items.Add(new Dictionary<string, object>
                {
                    { "id", view.Id.IntegerValue },
                    { "name", view.Name },
                    { "view_type", view.ViewType.ToString() }
                });
            }
            return new Dictionary<string, object> { { "kind", "views" }, { "items", items } };
        }

        private static Dictionary<string, object> QueryFamilies(
            Document document,
            IDictionary<string, object> arguments,
            int limit)
        {
            string nameContains = PlanValues.String(arguments, null, "name_contains", "name");
            IEnumerable<Family> families = new FilteredElementCollector(document)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .OrderBy(family => family.Name);
            if (!string.IsNullOrWhiteSpace(nameContains))
            {
                families = families.Where(family => family.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            var items = new List<Dictionary<string, object>>();
            foreach (Family family in families.Take(limit))
            {
                items.Add(new Dictionary<string, object>
                {
                    { "id", family.Id.IntegerValue },
                    { "name", family.Name },
                    { "placement_type", family.FamilyPlacementType.ToString() },
                    { "symbol_ids", family.GetFamilySymbolIds().Select(id => id.IntegerValue).ToArray() }
                });
            }
            return new Dictionary<string, object> { { "kind", "families" }, { "items", items } };
        }

        private static Dictionary<string, object> QueryTypes(
            Document document,
            IDictionary<string, object> arguments,
            int limit,
            bool mepOnly)
        {
            string nameContains = PlanValues.String(arguments, null, "name_contains", "name");
            string familyName = PlanValues.String(arguments, null, "family", "family_name");
            object category = PlanValues.Get(arguments, "category", "category_id");
            IEnumerable<ElementType> types = new FilteredElementCollector(document)
                .WhereElementIsElementType()
                .Cast<ElementType>();
            if (category != null)
            {
                ElementId categoryId = RevitLookups.ResolveCategoryId(document, arguments, BuiltInCategory.OST_GenericModel);
                types = types.Where(type => type.Category != null && type.Category.Id.IntegerValue == categoryId.IntegerValue);
            }
            if (mepOnly)
            {
                types = types.Where(IsMepType);
            }
            if (!string.IsNullOrWhiteSpace(nameContains))
            {
                types = types.Where(type => RevitLookups.ElementName(type).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            if (!string.IsNullOrWhiteSpace(familyName))
            {
                types = types.Where(type => RevitLookups.FamilyName(type).IndexOf(familyName, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            var items = new List<Dictionary<string, object>>();
            foreach (ElementType type in types.OrderBy(RevitLookups.ElementName).Take(limit))
            {
                items.Add(new Dictionary<string, object>
                {
                    { "id", type.Id.IntegerValue },
                    { "name", RevitLookups.ElementName(type) },
                    { "class", type.GetType().FullName },
                    { "category", type.Category == null ? null : type.Category.Name },
                    { "family_name", RevitLookups.FamilyName(type) }
                });
            }
            return new Dictionary<string, object> { { "kind", mepOnly ? "mep_types" : "types" }, { "items", items } };
        }

        private static bool IsMepType(ElementType type)
        {
            return type is PipeType || type is DuctType || type is ConduitType || type is CableTrayType ||
                   type is PipingSystemType || type is MechanicalSystemType;
        }

        private static string TypeName(Document document, Element element)
        {
            if (element is ElementType)
            {
                return RevitLookups.ElementName(element);
            }
            Element type = document.GetElement(element.GetTypeId());
            return RevitLookups.ElementName(type);
        }

        private static string FamilyName(Document document, Element element)
        {
            if (element is ElementType)
            {
                return RevitLookups.FamilyName(element);
            }
            Element type = document.GetElement(element.GetTypeId());
            return RevitLookups.FamilyName(type);
        }

        private static int ReadLimit(IDictionary<string, object> arguments)
        {
            int limit = PlanValues.Integer(arguments, 100, "limit");
            if (limit < 1 || limit > 500)
            {
                throw new BridgeCommandException("limit 必须在 1 到 500 之间。");
            }
            return limit;
        }

        private static List<string> ReadStringList(object value)
        {
            var result = new List<string>();
            if (value == null)
            {
                return result;
            }
            string single = value as string;
            if (single != null)
            {
                result.Add(single);
                return result;
            }
            IEnumerable enumerable = value as IEnumerable;
            if (enumerable == null)
            {
                throw new BridgeCommandException("parameters 必须是字符串或字符串数组。");
            }
            foreach (object item in enumerable)
            {
                string name = Convert.ToString(item);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result.Add(name.Trim());
                }
            }
            return result;
        }
    }
}
