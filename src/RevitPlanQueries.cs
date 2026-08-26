using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;

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
                { "active_view_id", document.ActiveView == null ? (object)null : document.ActiveView.Id.Value },
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
                case "sheets":
                    return QuerySheets(context.Document, limit);
                case "schedules":
                    return QuerySchedules(context.Document, limit);
                case "view_types":
                    return QueryViewTypes(context.Document, limit);
                case "title_blocks":
                case "titleblocks":
                    return QueryFamilySymbolsByCategory(context.Document, BuiltInCategory.OST_TitleBlocks, "title_blocks", limit);
                case "text_types":
                    return QueryTextTypes(context.Document, limit);
                case "filled_region_types":
                    return QueryFilledRegionTypes(context.Document, limit);
                case "revisions":
                    return QueryRevisions(context.Document, limit);
                case "families":
                    return QueryFamilies(context.Document, step.Arguments, limit);
                case "links":
                    return QueryLinks(context.Document, limit);
                case "types":
                    return QueryTypes(context.Document, step.Arguments, limit, false);
                case "mep_types":
                    return QueryTypes(context.Document, step.Arguments, limit, true);
                default:
                    throw new BridgeCommandException("query_catalog.kind 仅支持 levels、categories、views、sheets、schedules、view_types、title_blocks、text_types、filled_region_types、revisions、families、types、mep_types、links。");
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

            List<Element> materialized = candidates.OrderBy(element => element.Id.Value).Take(limit + 1).ToList();
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

        public static Dictionary<string, object> QueryReferences(PlanStep step, PlanExecutionContext context)
        {
            IList<ElementId> ids = context.ResolveElementIds(step.Arguments, "element_ids", "ids", "targets");
            int limit = ReadLimit(step.Arguments);
            string kind = PlanValues.String(step.Arguments, "all", "kind", "reference_kind")
                .Trim().ToLowerInvariant();
            if (kind != "all" && kind != "faces" && kind != "edges")
            {
                throw new BridgeCommandException("query_references.kind 仅支持 all、faces、edges。");
            }
            var options = new Options
            {
                ComputeReferences = true,
                IncludeNonVisibleObjects = false,
                DetailLevel = ViewDetailLevel.Fine
            };
            int remaining = limit;
            var items = new List<Dictionary<string, object>>();
            foreach (ElementId id in ids)
            {
                if (remaining <= 0) break;
                Element element = context.Document.GetElement(id);
                if (element == null)
                {
                    throw new BridgeCommandException("找不到 element_id=" + id.Value + " 对应元素。");
                }
                var references = new List<Dictionary<string, object>>();
                CollectStableReferences(
                    context.Document,
                    element.get_Geometry(options),
                    kind,
                    references,
                    ref remaining);
                items.Add(new Dictionary<string, object>
                {
                    { "element_id", id.Value },
                    { "references", references }
                });
            }
            return new Dictionary<string, object>
            {
                { "kind", kind },
                { "count", items.Sum(item => ((List<Dictionary<string, object>>)item["references"]).Count) },
                { "truncated", remaining <= 0 },
                { "items", items }
            };
        }

        public static Dictionary<string, object> QueryParameters(PlanStep step, PlanExecutionContext context)
        {
            ElementId targetId = context.ResolveSingleElementId(step.Arguments, "element_id", "element", "id", "targets");
            if (targetId.Value == ElementId.InvalidElementId.Value)
            {
                return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置元素引用尚无真实 ID。" } };
            }
            Element element = context.Document.GetElement(targetId);
            if (element == null)
            {
                throw new BridgeCommandException("query_parameters 找不到 element_id=" + targetId.Value + "。");
            }
            string nameContains = PlanValues.String(step.Arguments, null, "name_contains", "name_like");
            bool includeReadOnly = PlanValues.Boolean(step.Arguments, true, "include_read_only");

            var parameters = new List<Dictionary<string, object>>();
            foreach (Parameter parameter in element.Parameters)
            {
                if (parameter.Definition == null)
                {
                    continue;
                }
                string parameterName = parameter.Definition.Name ?? string.Empty;
                if (!includeReadOnly && parameter.IsReadOnly)
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(nameContains) &&
                    parameterName.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                var item = new Dictionary<string, object>
                {
                    { "name", parameterName },
                    { "group", GetParameterGroupName(parameter) }
                };
                InternalDefinition internalDefinition = parameter.Definition as InternalDefinition;
                if (internalDefinition != null && internalDefinition.BuiltInParameter != BuiltInParameter.INVALID)
                {
                    item["built_in_id"] = internalDefinition.BuiltInParameter.ToString();
                }
                foreach (KeyValuePair<string, object> pair in RevitLookups.ParameterData(parameter))
                {
                    item[pair.Key] = pair.Value;
                }
                parameters.Add(item);
            }
            return new Dictionary<string, object>
            {
                { "element_id", targetId.Value },
                { "count", parameters.Count },
                { "parameters", parameters }
            };
        }

        public static Dictionary<string, object> QueryGeometry(PlanStep step, PlanExecutionContext context)
        {
            ElementId targetId = context.ResolveSingleElementId(step.Arguments, "element_id", "element", "id", "targets");
            if (targetId.Value == ElementId.InvalidElementId.Value)
            {
                return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置元素引用尚无真实 ID。" } };
            }
            Element element = context.Document.GetElement(targetId);
            if (element == null)
            {
                throw new BridgeCommandException("query_geometry 找不到 element_id=" + targetId.Value + "。");
            }
            string detail = PlanValues.String(step.Arguments, "bbox", "detail", "level").Trim().ToLowerInvariant();
            if (detail != "bbox" && detail != "solid_summary" && detail != "faces")
            {
                throw new BridgeCommandException("query_geometry.detail 仅支持 bbox、solid_summary、faces。");
            }
            int limit = ReadLimit(step.Arguments);

            var data = new Dictionary<string, object>
            {
                { "element_id", targetId.Value },
                { "detail", detail }
            };
            BoundingBoxXYZ box = element.get_BoundingBox(null);
            if (box != null)
            {
                data["bounding_box"] = new Dictionary<string, object>
                {
                    { "min", PlanValues.PointData(box.Min) },
                    { "max", PlanValues.PointData(box.Max) },
                    { "center", PlanValues.PointData(box.Min.Add(box.Max).Multiply(0.5)) },
                    { "size", PlanValues.PointData(box.Max.Subtract(box.Min)) }
                };
            }
            if (detail == "bbox")
            {
                return data;
            }

            var options = new Options
            {
                ComputeReferences = false,
                IncludeNonVisibleObjects = false,
                DetailLevel = ViewDetailLevel.Fine
            };
            List<Solid> solids = new List<Solid>();
            CollectSolids(element.get_Geometry(options), solids);
            double volume = solids.Sum(solid => solid.Volume);
            double area = solids.Sum(solid => solid.SurfaceArea);
            double volumeMm3 = volume * 1000.0 * 1000.0 * 1000.0 / (304.8 * 304.8 * 304.8);
            double areaMm2 = area * 1000.0 * 1000.0 / (304.8 * 304.8);
            data["solid_count"] = solids.Count;
            data["volume_mm3"] = Math.Round(volumeMm3, 3, MidpointRounding.AwayFromZero);
            data["surface_area_mm2"] = Math.Round(areaMm2, 3, MidpointRounding.AwayFromZero);
            data["face_count"] = solids.Sum(solid => solid.Faces.Size);
            if (detail == "solid_summary")
            {
                return data;
            }

            var faces = new List<Dictionary<string, object>>();
            bool truncated = false;
            foreach (Solid solid in solids)
            {
                foreach (Face face in solid.Faces)
                {
                    if (faces.Count >= limit)
                    {
                        truncated = true;
                        break;
                    }
                    var faceData = new Dictionary<string, object>
                    {
                        { "kind", face.GetType().Name },
                        { "area_mm2", Math.Round(face.Area * 1000.0 * 1000.0 / (304.8 * 304.8), 3, MidpointRounding.AwayFromZero) }
                    };
                    PlanarFace planarFace = face as PlanarFace;
                    if (planarFace != null)
                    {
                        faceData["normal"] = PlanValues.PointData(planarFace.FaceNormal);
                        faceData["origin"] = PlanValues.PointData(planarFace.Origin);
                    }
                    faces.Add(faceData);
                }
                if (truncated)
                {
                    break;
                }
            }
            data["faces"] = faces;
            data["truncated"] = truncated;
            return data;
        }

        public static Dictionary<string, object> QueryRoom(PlanStep step, PlanExecutionContext context)
        {
            Document document = context.Document;
            int limit = ReadLimit(step.Arguments);
            object rawPoint = PlanValues.Get(step.Arguments, "point");
            if (rawPoint != null)
            {
                XYZ point = PlanValues.Point(step.Arguments, "point");
                Room roomAtPoint = document.GetRoomAtPoint(point);
                var pointData = new Dictionary<string, object>
                {
                    { "mode", "at_point" },
                    { "point", PlanValues.PointData(point) },
                    { "room", roomAtPoint == null ? null : RoomData(roomAtPoint) }
                };
                return pointData;
            }

            var rooms = new FilteredElementCollector(document)
                .OfClass(typeof(Room))
                .Cast<Room>()
                .Where(room => room.IsValidObject)
                .OrderBy(room => room.Number)
                .Take(limit)
                .ToList();
            var items = new List<Dictionary<string, object>>();
            foreach (Room room in rooms)
            {
                items.Add(RoomData(room));
            }
            return new Dictionary<string, object>
            {
                { "mode", "list" },
                { "count", items.Count },
                { "rooms", items }
            };
        }

        public static Dictionary<string, object> QueryMepNetwork(PlanStep step, PlanExecutionContext context)
        {
            ElementId seedId = context.ResolveSingleElementId(step.Arguments, "element_id", "seed", "target", "targets");
            if (seedId.Value == ElementId.InvalidElementId.Value)
            {
                return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置元素引用尚无真实 ID。" } };
            }
            Element seed = context.Document.GetElement(seedId);
            if (seed == null)
            {
                throw new BridgeCommandException("query_mep_network 找不到 element_id=" + seedId.Value + "。");
            }
            int maxDepth = PlanValues.Integer(step.Arguments, 100, "max_depth");
            if (maxDepth < 1 || maxDepth > 2000)
            {
                throw new BridgeCommandException("max_depth 必须在 1 到 2000 之间。");
            }
            if (GetConnectorManager(seed) == null)
            {
                throw new BridgeCommandException("种子元素没有 MEP 连接件（必须是管道 / 风管 / 线管 / 桥架 / 配件）。");
            }

            var visited = new HashSet<ElementId>();
            var edges = new List<Dictionary<string, object>>();
            var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<ElementId>();
            queue.Enqueue(seedId);
            while (queue.Count > 0 && visited.Count < maxDepth)
            {
                ElementId currentId = queue.Dequeue();
                if (!visited.Add(currentId))
                {
                    continue;
                }
                Element current = context.Document.GetElement(currentId);
                if (current == null)
                {
                    continue;
                }
                ConnectorManager manager = GetConnectorManager(current);
                if (manager == null)
                {
                    continue;
                }
                foreach (Connector connector in manager.Connectors)
                {
                    foreach (Connector reference in connector.AllRefs)
                    {
                        Element owner = reference.Owner;
                        if (owner == null || visited.Contains(owner.Id))
                        {
                            continue;
                        }
                        long low = Math.Min(currentId.Value, owner.Id.Value);
                        long high = Math.Max(currentId.Value, owner.Id.Value);
                        string key = low + "-" + high;
                        if (edgeKeys.Add(key))
                        {
                            edges.Add(new Dictionary<string, object>
                            {
                                { "from", currentId.Value },
                                { "to", owner.Id.Value },
                                { "at", PlanValues.PointData(connector.Origin) }
                            });
                        }
                        if (!queue.Contains(owner.Id))
                        {
                            queue.Enqueue(owner.Id);
                        }
                    }
                }
            }

            var nodes = new List<Dictionary<string, object>>();
            foreach (ElementId id in visited)
            {
                Element element = context.Document.GetElement(id);
                if (element == null)
                {
                    continue;
                }
                var node = new Dictionary<string, object>
                {
                    { "element_id", id.Value },
                    { "class", element.GetType().Name },
                    { "category", element.Category == null ? null : element.Category.Name }
                };
                string systemName = ReadSystemName(element);
                if (systemName != null)
                {
                    node["system_name"] = systemName;
                }
                nodes.Add(node);
            }
            return new Dictionary<string, object>
            {
                { "seed", seedId.Value },
                { "max_depth", maxDepth },
                { "node_count", nodes.Count },
                { "nodes", nodes },
                { "edges", edges }
            };
        }

        private static ConnectorManager GetConnectorManager(Element element)
        {
            MEPCurve curve = element as MEPCurve;
            if (curve != null)
            {
                return curve.ConnectorManager;
            }
            FamilyInstance instance = element as FamilyInstance;
            if (instance != null && instance.MEPModel != null)
            {
                return instance.MEPModel.ConnectorManager;
            }
            return null;
        }

        private static string ReadSystemName(Element element)
        {
            try
            {
                string[] systemParams = {
                    "RBS_PIPING_SYSTEM_NAME_PARAM",
                    "RBS_DUCT_SYSTEM_NAME_PARAM"
                };
                foreach (string paramName in systemParams)
                {
                    BuiltInParameter bip;
                    if (Enum.TryParse(paramName, out bip))
                    {
                        Parameter p = element.get_Parameter(bip);
                        if (p != null)
                        {
                            string val = p.AsString() ?? p.AsValueString();
                            if (!string.IsNullOrEmpty(val)) return val;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

private static string GetParameterGroupName(Parameter parameter)
        {
            return "";
        }

        public static Dictionary<string, object> QueryViewRange(PlanStep step, PlanExecutionContext context)
        {
            ElementId viewId = context.ResolveSingleElementId(step.Arguments, "view_id", "view");
            if (viewId.Value == ElementId.InvalidElementId.Value)
            {
                return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置视图引用尚无真实 ID。" } };
            }
            ViewPlan viewPlan = context.Document.GetElement(viewId) as ViewPlan;
            if (viewPlan == null || viewPlan.IsTemplate)
            {
                throw new BridgeCommandException("query_view_range 的 view_id 必须指向平面视图。");
            }
            PlanViewRange range = viewPlan.GetViewRange();
            var ranges = new Dictionary<string, object>();
            ranges["top"] = RangeSlotData(context.Document, range, 0);       // Top
            ranges["cut_plane"] = RangeSlotData(context.Document, range, 1); // CutPlane
            ranges["bottom"] = RangeSlotData(context.Document, range, 2);    // Bottom
            ranges["view_depth"] = RangeSlotData(context.Document, range, 3);// ViewDepth
            return new Dictionary<string, object>
            {
                { "view_id", viewId.Value },
                { "view_name", viewPlan.Name },
                { "ranges", ranges }
            };
        }

        private static Dictionary<string, object> RangeSlotData(Document document, PlanViewRange range, int rangeType)
        {
int slot = rangeType;
            ElementId levelId = range.GetLevelId((PlanViewPlane)slot);
            Level level = levelId.Value == ElementId.InvalidElementId.Value
                ? null
                : document.GetElement(levelId) as Level;
            return new Dictionary<string, object>
            {
                { "level", level == null ? null : level.Name },
                { "level_id", level == null ? (object)null : levelId.Value },
{ "offset_mm", PlanValues.ToMillimeters(range.GetOffset((PlanViewPlane)slot)) }
            };
        }

        public static Dictionary<string, object> CheckInterferences(PlanStep step, PlanExecutionContext context)
        {
            Document document = context.Document;
            IList<ElementId> candidates = context.ResolveElementIds(step.Arguments, "element_ids", "elements", "targets");
            if (candidates.Count == 0)
            {
                return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置元素引用尚无真实 ID。" } };
            }
            bool includeLinks = PlanValues.Boolean(step.Arguments, false, "include_links", "links");
            int limit = ReadLimit(step.Arguments);
            List<ElementId> against = null;
            object rawAgainst = PlanValues.Get(step.Arguments, "against_ids", "against");
            if (rawAgainst != null)
            {
                against = context.ResolveElementIds(step.Arguments, "against_ids", "against").ToList();
            }
            if (against == null && candidates.Count > 500)
            {
                throw new BridgeCommandException("候选元素超过 500 个；请提供 against_ids 缩小对照集。");
            }

            var interferences = new List<Dictionary<string, object>>();
            bool truncated = false;

            if (against == null)
            {
                for (int i = 0; i < candidates.Count && !truncated; i++)
                {
                    for (int j = i + 1; j < candidates.Count; j++)
                    {
                        if (AddInterference(document, candidates[i], candidates[j], "current", "current", interferences))
                        {
                            if (interferences.Count >= limit)
                            {
                                truncated = true;
                                break;
                            }
                        }
                    }
                }
            }
            else
            {
                foreach (ElementId candidateId in candidates)
                {
                    foreach (ElementId againstId in against)
                    {
                        if (candidateId.Value == againstId.Value)
                        {
                            continue;
                        }
                        if (AddInterference(document, candidateId, againstId, "current", "current", interferences))
                        {
                            if (interferences.Count >= limit)
                            {
                                truncated = true;
                                break;
                            }
                        }
                    }
                    if (truncated)
                    {
                        break;
                    }
                }
            }

            var linkDocuments = new List<Dictionary<string, object>>();
            if (includeLinks)
            {
                foreach (RevitLinkInstance link in new FilteredElementCollector(document)
                    .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    Document linkDocument = link.GetLinkDocument();
                    if (linkDocument == null)
                    {
                        continue;
                    }
                    string linkName = link.Name;
                    Transform totalTransform = link.GetTotalTransform();
                    foreach (ElementId candidateId in candidates)
                    {
                        Element candidate = document.GetElement(candidateId);
                        if (candidate == null)
                        {
                            continue;
                        }
                        List<Solid> candidateSolids = new List<Solid>();
                        var geometryOptions = new Options
                        {
                            ComputeReferences = false,
                            IncludeNonVisibleObjects = false,
                            DetailLevel = ViewDetailLevel.Fine
                        };
                        CollectSolids(candidate.get_Geometry(geometryOptions), candidateSolids);
                        if (candidateSolids.Count == 0)
                        {
                            continue;
                        }
                        foreach (Solid solid in candidateSolids)
                        {
                            Solid linkSpaceSolid = SolidUtils.CreateTransformed(solid, totalTransform.Inverse);
                            foreach (Element linkElement in new FilteredElementCollector(linkDocument)
                                .WhereElementIsNotElementType()
                                .WherePasses(new ElementIntersectsSolidFilter(linkSpaceSolid)))
                            {
                                interferences.Add(new Dictionary<string, object>
                                {
                                    { "element_a", candidateId.Value },
                                    { "element_b", linkElement.Id.Value },
                                    { "document_a", "current" },
                                    { "document_b", "link:" + linkName },
                                    { "category_a", CategoryName(candidate) },
                                    { "category_b", CategoryName(linkElement) }
                                });
                                if (interferences.Count >= limit)
                                {
                                    truncated = true;
                                    break;
                                }
                            }
                            if (truncated)
                            {
                                break;
                            }
                        }
                        if (truncated)
                        {
                            break;
                        }
                    }
                    linkDocuments.Add(new Dictionary<string, object>
                    {
                        { "name", linkName },
                        { "checked", true }
                    });
                    if (truncated)
                    {
                        break;
                    }
                }
            }

            return new Dictionary<string, object>
            {
                { "count", interferences.Count },
                { "truncated", truncated },
                { "include_links", includeLinks },
                { "links_checked", linkDocuments },
                { "interferences", interferences }
            };
        }

        private static bool AddInterference(
            Document document,
            ElementId firstId,
            ElementId secondId,
            string firstDocument,
            string secondDocument,
            List<Dictionary<string, object>> interferences)
        {
            Element first = document.GetElement(firstId);
            Element second = document.GetElement(secondId);
            if (first == null || second == null || firstId.Value == secondId.Value)
            {
                return false;
            }
            ElementIntersectsElementFilter filter = new ElementIntersectsElementFilter(first);
            if (!filter.PassesFilter(second))
            {
                return false;
            }
            var item = new Dictionary<string, object>
            {
                { "element_a", firstId.Value },
                { "element_b", secondId.Value },
                { "document_a", firstDocument },
                { "document_b", secondDocument },
                { "category_a", CategoryName(first) },
                { "category_b", CategoryName(second) }
            };
            try
            {
                Solid firstSolid = FindPrimarySolid(first);
                Solid secondSolid = FindPrimarySolid(second);
                if (firstSolid != null && secondSolid != null)
                {
                    Solid overlap = BooleanOperationsUtils.ExecuteBooleanOperation(
                        firstSolid, secondSolid, BooleanOperationsType.Intersect);
                    if (overlap != null && overlap.Volume > 1e-12)
                    {
                        item["overlap_volume_mm3"] = Math.Round(
                            overlap.Volume * 1000.0 * 1000.0 * 1000.0 / (304.8 * 304.8 * 304.8),
                            3, MidpointRounding.AwayFromZero);
                    }
                }
            }
            catch
            {
                // 布尔求交失败不影响"存在碰撞"的结论，仅省略体积。
            }
            interferences.Add(item);
            return true;
        }

        private static Solid FindPrimarySolid(Element element)
        {
            var options = new Options
            {
                ComputeReferences = false,
                IncludeNonVisibleObjects = false,
                DetailLevel = ViewDetailLevel.Fine
            };
            List<Solid> solids = new List<Solid>();
            CollectSolids(element.get_Geometry(options), solids);
            return solids.OrderByDescending(solid => solid.Volume).FirstOrDefault();
        }

        private static void CollectSolids(GeometryElement geometry, ICollection<Solid> target)
        {
            if (geometry == null)
            {
                return;
            }
            foreach (GeometryObject item in geometry)
            {
                Solid solid = item as Solid;
                if (solid != null && solid.Volume > 1e-12)
                {
                    target.Add(solid);
                    continue;
                }
                GeometryInstance instance = item as GeometryInstance;
                if (instance != null)
                {
                    CollectSolids(instance.GetInstanceGeometry(), target);
                }
            }
        }

        private static string CategoryName(Element element)
        {
            return element.Category == null ? null : element.Category.Name;
        }

        private static Dictionary<string, object> RoomData(Room room)
        {
            var data = new Dictionary<string, object>
            {
                { "element_id", room.Id.Value },
                { "name", room.Name },
                { "number", room.Number },
                { "level", room.Level == null ? null : room.Level.Name },
                { "area_mm2", Math.Round(room.Area * 92903.04, 3, MidpointRounding.AwayFromZero) }
            };
            var boundary = new List<List<Dictionary<string, object>>>();
            try
            {
                foreach (IList<BoundarySegment> loop in room.GetBoundarySegments(new SpatialElementBoundaryOptions()))
                {
                    var loopPoints = new List<Dictionary<string, object>>();
                    foreach (BoundarySegment segment in loop)
                    {
                        loopPoints.Add(PlanValues.PointData(segment.GetCurve().GetEndPoint(0)));
                    }
                    boundary.Add(loopPoints);
                }
            }
            catch
            {
                // 无边界（未放置的房间）时省略 boundary。
            }
            data["boundary"] = boundary;
            return data;
        }

        private static Dictionary<string, object> QueryLinks(Document document, int limit)
        {
            var items = new List<Dictionary<string, object>>();
            foreach (RevitLinkInstance link in new FilteredElementCollector(document)
                .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().Take(limit))
            {
                RevitLinkType linkType = document.GetElement(link.GetTypeId()) as RevitLinkType;
                items.Add(new Dictionary<string, object>
                {
                    { "id", link.Id.Value },
                    { "name", link.Name },
                    { "status", linkType == null ? null : linkType.GetLinkedFileStatus().ToString() },
                    { "has_link_document", link.GetLinkDocument() != null },
                    { "instance_transform_origin", PlanValues.PointData(link.GetTotalTransform().Origin) }
                });
            }
            return new Dictionary<string, object> { { "kind", "links" }, { "items", items } };
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
                    { "id", level.Id.Value },
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
                    { "id", category.Id.Value },
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
                    { "id", view.Id.Value },
                    { "name", view.Name },
                    { "view_type", view.ViewType.ToString() }
                });
            }
            return new Dictionary<string, object> { { "kind", "views" }, { "items", items } };
        }

        private static Dictionary<string, object> QuerySheets(Document document, int limit)
        {
            var items = new List<Dictionary<string, object>>();
            foreach (ViewSheet sheet in new FilteredElementCollector(document)
                .OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .OrderBy(sheet => sheet.SheetNumber).Take(limit))
            {
                items.Add(new Dictionary<string, object>
                {
                    { "id", sheet.Id.Value },
                    { "sheet_number", sheet.SheetNumber },
                    { "name", sheet.Name }
                });
            }
            return new Dictionary<string, object> { { "kind", "sheets" }, { "items", items } };
        }

        private static Dictionary<string, object> QuerySchedules(Document document, int limit)
        {
            var items = new List<Dictionary<string, object>>();
            foreach (ViewSchedule schedule in new FilteredElementCollector(document)
                .OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(schedule => !schedule.IsTemplate)
                .OrderBy(schedule => schedule.Name).Take(limit))
            {
                items.Add(new Dictionary<string, object>
                {
                    { "id", schedule.Id.Value },
                    { "name", schedule.Name },
                    { "category_id", schedule.Definition == null ? (object)null : schedule.Definition.CategoryId.Value },
                    { "field_count", schedule.Definition == null ? 0 : schedule.Definition.GetFieldCount() }
                });
            }
            return new Dictionary<string, object> { { "kind", "schedules" }, { "items", items } };
        }

        private static Dictionary<string, object> QueryViewTypes(Document document, int limit)
        {
            var items = new List<Dictionary<string, object>>();
            foreach (ViewFamilyType type in new FilteredElementCollector(document)
                .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .OrderBy(type => type.ViewFamily.ToString()).ThenBy(type => type.Name).Take(limit))
            {
                items.Add(new Dictionary<string, object>
                {
                    { "id", type.Id.Value },
                    { "name", type.Name },
                    { "view_family", type.ViewFamily.ToString() }
                });
            }
            return new Dictionary<string, object> { { "kind", "view_types" }, { "items", items } };
        }

        private static Dictionary<string, object> QueryTextTypes(Document document, int limit)
        {
            var items = new List<Dictionary<string, object>>();
            foreach (TextNoteType type in new FilteredElementCollector(document)
                .OfClass(typeof(TextNoteType)).Cast<TextNoteType>()
                .OrderBy(type => type.Name).Take(limit))
            {
                items.Add(new Dictionary<string, object>
                {
                    { "id", type.Id.Value },
                    { "name", type.Name }
                });
            }
            return new Dictionary<string, object> { { "kind", "text_types" }, { "items", items } };
        }

        private static Dictionary<string, object> QueryFilledRegionTypes(Document document, int limit)
        {
            var items = new List<Dictionary<string, object>>();
            foreach (FilledRegionType type in new FilteredElementCollector(document)
                .OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>()
                .OrderBy(type => type.Name).Take(limit))
            {
                items.Add(new Dictionary<string, object>
                {
                    { "id", type.Id.Value },
                    { "name", type.Name }
                });
            }
            return new Dictionary<string, object> { { "kind", "filled_region_types" }, { "items", items } };
        }

        private static Dictionary<string, object> QueryRevisions(Document document, int limit)
        {
            var items = new List<Dictionary<string, object>>();
            foreach (Revision revision in new FilteredElementCollector(document)
                .OfClass(typeof(Revision)).Cast<Revision>()
                .OrderBy(revision => revision.SequenceNumber).Take(limit))
            {
                items.Add(new Dictionary<string, object>
                {
                    { "id", revision.Id.Value },
                    { "number", revision.RevisionNumber },
                    { "description", revision.Description },
                    { "date", revision.RevisionDate },
                    { "issued", revision.Issued },
                    { "visibility", revision.Visibility.ToString() }
                });
            }
            return new Dictionary<string, object> { { "kind", "revisions" }, { "items", items } };
        }

        private static Dictionary<string, object> QueryFamilySymbolsByCategory(
            Document document,
            BuiltInCategory category,
            string kind,
            int limit)
        {
            ElementId categoryId = new ElementId(category);
            var items = new List<Dictionary<string, object>>();
            foreach (FamilySymbol type in new FilteredElementCollector(document)
                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(type => type.Category != null && type.Category.Id.Value == categoryId.Value)
                .OrderBy(type => RevitLookups.FamilyName(type)).ThenBy(type => type.Name).Take(limit))
            {
                items.Add(new Dictionary<string, object>
                {
                    { "id", type.Id.Value },
                    { "family", RevitLookups.FamilyName(type) },
                    { "name", type.Name }
                });
            }
            return new Dictionary<string, object> { { "kind", kind }, { "items", items } };
        }

        private static void CollectStableReferences(
            Document document,
            GeometryElement geometry,
            string kind,
            ICollection<Dictionary<string, object>> target,
            ref int remaining)
        {
            if (geometry == null || remaining <= 0) return;
            foreach (GeometryObject item in geometry)
            {
                if (remaining <= 0) return;
                Solid solid = item as Solid;
                if (solid != null && solid.Volume > 1e-12)
                {
                    if (kind == "all" || kind == "faces")
                    {
                        foreach (Face face in solid.Faces)
                        {
                            AddStableReference(document, face.Reference, "face", target, ref remaining);
                            if (remaining <= 0) return;
                        }
                    }
                    if (kind == "all" || kind == "edges")
                    {
                        foreach (Edge edge in solid.Edges)
                        {
                            AddStableReference(document, edge.Reference, "edge", target, ref remaining);
                            if (remaining <= 0) return;
                        }
                    }
                    continue;
                }
                GeometryInstance instance = item as GeometryInstance;
                if (instance != null)
                {
                    CollectStableReferences(document, instance.GetInstanceGeometry(), kind, target, ref remaining);
                }
            }
        }

        private static void AddStableReference(
            Document document,
            Reference reference,
            string kind,
            ICollection<Dictionary<string, object>> target,
            ref int remaining)
        {
            if (reference == null || remaining <= 0) return;
            try
            {
                string stable = reference.ConvertToStableRepresentation(document);
                if (string.IsNullOrWhiteSpace(stable)) return;
                target.Add(new Dictionary<string, object>
                {
                    { "kind", kind },
                    { "stable_reference", stable }
                });
                remaining--;
            }
            catch
            {
                // 有些导入或链接几何不提供可持久化引用；跳过即可。
            }
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
                    { "id", family.Id.Value },
                    { "name", family.Name },
                    { "placement_type", family.FamilyPlacementType.ToString() },
                    { "symbol_ids", family.GetFamilySymbolIds().Select(id => id.Value).ToArray() }
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
                types = types.Where(type => type.Category != null && type.Category.Id.Value == categoryId.Value);
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
                    { "id", type.Id.Value },
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

public static Dictionary<string, object> QuerySelection(PlanStep step, PlanExecutionContext context)
        {
            UIDocument uiDoc = context.UiApplication.ActiveUIDocument;
            if (uiDoc == null || context.Document == null)
            {
                return new Dictionary<string, object>
                {
                    { "selected_ids", Array.Empty<long>() },
                    { "count", 0 },
                    { "elements", Array.Empty<object>() }
                };
            }

            ICollection<ElementId> ids = uiDoc.Selection.GetElementIds();
            var elements = new List<Dictionary<string, object>>();
            foreach (ElementId id in ids)
            {
                Element e = context.Document.GetElement(id);
                if (e == null) continue;

                elements.Add(new Dictionary<string, object>
                {
                    { "element_id", id.Value },
                    { "unique_id", e.UniqueId },
                    { "category", e.Category?.Name },
                    { "name", RevitLookups.ElementName(e) },
                    { "type", e.GetType().Name }
                });
            }

            return new Dictionary<string, object>
            {
                { "selected_ids", ids.Select(id => (long)id.Value).ToArray() },
                { "count", ids.Count },
                { "elements", elements }
            };
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
