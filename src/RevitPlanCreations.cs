using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;

namespace RevitCommandBridge
{
    internal static class RevitPlanCreations
    {
        public static Dictionary<string, object> CreateLevel(PlanStep step, PlanExecutionContext context)
        {
            double elevationMm = PlanValues.RequireMillimeters(step.Arguments, "elevation_mm", "elevation");
            string name = PlanValues.String(step.Arguments, null, "name");
            if (!string.IsNullOrWhiteSpace(name) && new FilteredElementCollector(context.Document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .Any(existingLevel => string.Equals(existingLevel.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new BridgeCommandException("标高名称已存在：" + name);
            }
            var data = new Dictionary<string, object>
            {
                { "name", name },
                { "elevation_mm", elevationMm }
            };
            if (context.Preview)
            {
                return data;
            }

            Level level = Level.Create(context.Document, PlanValues.ToFeet(elevationMm));
            if (!string.IsNullOrWhiteSpace(name))
            {
                level.Name = name;
            }
            data["element_id"] = level.Id.IntegerValue;
            data["element_ids"] = new[] { level.Id.IntegerValue };
            data["name"] = level.Name;
            return data;
        }

        public static Dictionary<string, object> CreateGrid(PlanStep step, PlanExecutionContext context)
        {
            XYZ start = PlanValues.Point(step.Arguments, "start");
            XYZ end = PlanValues.Point(step.Arguments, "end");
            string name = PlanValues.String(step.Arguments, null, "name");
            if (start.DistanceTo(end) < 1e-8)
            {
                throw new BridgeCommandException("轴网起点与终点不能重合。");
            }
            if (Math.Abs(start.Z - end.Z) > 1e-8)
            {
                throw new BridgeCommandException("直线轴网的 start.z 与 end.z 必须相同。");
            }
            var data = new Dictionary<string, object>
            {
                { "name", name },
                { "start", PlanValues.PointData(start) },
                { "end", PlanValues.PointData(end) }
            };
            if (context.Preview)
            {
                return data;
            }

            Grid grid = Grid.Create(context.Document, Line.CreateBound(start, end));
            if (!string.IsNullOrWhiteSpace(name))
            {
                grid.Name = name;
            }
            data["element_id"] = grid.Id.IntegerValue;
            data["element_ids"] = new[] { grid.Id.IntegerValue };
            data["name"] = grid.Name;
            return data;
        }

        public static Dictionary<string, object> CreateWall(PlanStep step, PlanExecutionContext context)
        {
            XYZ start = PlanValues.Point(step.Arguments, "start");
            XYZ end = PlanValues.Point(step.Arguments, "end");
            double heightMm = PlanValues.Millimeters(step.Arguments, 3000.0, "height_mm", "height");
            double baseOffsetMm = PlanValues.Millimeters(step.Arguments, 0.0, "base_offset_mm", "base_offset");
            if (start.DistanceTo(end) < 1e-8)
            {
                throw new BridgeCommandException("墙体起点与终点不能重合。");
            }
            if (heightMm <= 0.0)
            {
                throw new BridgeCommandException("墙体 height_mm 必须大于 0。");
            }

            Level level = RevitLookups.ResolveLevel(context.Document, step.Arguments);
            WallType sourceType = (WallType)RevitLookups.ResolveElementType(
                context.Document, typeof(WallType), step.Arguments, true);
            if (sourceType.Kind != WallKind.Basic)
            {
                throw new BridgeCommandException("create_wall 目前只支持 Basic WallType。");
            }
            double requestedThicknessMm = PlanValues.Millimeters(step.Arguments, -1.0, "thickness_mm", "thickness");
            string requestedNewTypeName = PlanValues.String(step.Arguments, null, "new_type", "new_wall_type");
            string targetTypeName = requestedThicknessMm > 0.0
                ? (string.IsNullOrWhiteSpace(requestedNewTypeName)
                    ? "RCB_" + sourceType.Name + "_" + requestedThicknessMm.ToString("0.###", CultureInfo.InvariantCulture) + "mm"
                    : requestedNewTypeName)
                : sourceType.Name;

            var data = new Dictionary<string, object>
            {
                { "level", level.Name },
                { "type_source", sourceType.Name },
                { "type_target", targetTypeName },
                { "start", PlanValues.PointData(start) },
                { "end", PlanValues.PointData(end) },
                { "height_mm", heightMm },
                { "base_offset_mm", baseOffsetMm }
            };
            if (requestedThicknessMm > 0.0)
            {
                data["thickness_mm"] = requestedThicknessMm;
            }
            if (context.Preview)
            {
                return data;
            }

            WallType targetType = requestedThicknessMm > 0.0
                ? ResolveOrCreateWallType(context.Document, sourceType, targetTypeName, requestedThicknessMm)
                : sourceType;
            XYZ wallStart = new XYZ(start.X, start.Y, level.Elevation);
            XYZ wallEnd = new XYZ(end.X, end.Y, level.Elevation);
            Wall wall = Wall.Create(
                context.Document,
                Line.CreateBound(wallStart, wallEnd),
                targetType.Id,
                level.Id,
                PlanValues.ToFeet(heightMm),
                PlanValues.ToFeet(baseOffsetMm),
                false,
                false);
            data["element_id"] = wall.Id.IntegerValue;
            data["element_ids"] = new[] { wall.Id.IntegerValue };
            data["type_target"] = targetType.Name;
            return data;
        }

        public static Dictionary<string, object> CreateDirectShape(PlanStep step, PlanExecutionContext context)
        {
            ElementId categoryId = RevitLookups.ResolveCategoryId(
                context.Document, step.Arguments, BuiltInCategory.OST_GenericModel);
            if (!DirectShape.IsValidCategoryId(categoryId, context.Document))
            {
                throw new BridgeCommandException("DirectShape 不支持类别 ID：" + categoryId.IntegerValue);
            }

            string name = PlanValues.String(step.Arguments, step.Id, "name");
            string materialName = PlanValues.String(step.Arguments, null, "material", "material_name");
            bool createMaterial = PlanValues.Boolean(step.Arguments, false, "create_material_if_missing");
            Material material = RevitLookups.FindMaterial(context.Document, materialName);
            if (!string.IsNullOrWhiteSpace(materialName) && material == null && !createMaterial)
            {
                throw new BridgeCommandException("找不到材质“" + materialName + "”。设置 create_material_if_missing=true 可在执行时创建。");
            }
            Dictionary<string, object> geometryData = RevitGeometryFactory.DescribeGeometry(step.Arguments);
            var data = new Dictionary<string, object>
            {
                { "name", name },
                { "category_id", categoryId.IntegerValue },
                { "material", materialName },
                { "material_exists", material != null },
                { "geometry", geometryData }
            };
            if (context.Preview)
            {
                return data;
            }

            if (material == null && !string.IsNullOrWhiteSpace(materialName))
            {
                ElementId materialId = Material.Create(context.Document, materialName);
                material = context.Document.GetElement(materialId) as Material;
            }
            var options = new SolidOptions(
                material == null ? ElementId.InvalidElementId : material.Id,
                ElementId.InvalidElementId);
            IList<GeometryObject> geometry = RevitGeometryFactory.CreateGeometry(step.Arguments, options);
            DirectShape shape = DirectShape.CreateElement(context.Document, categoryId);
            if (!shape.IsValidShape(geometry))
            {
                throw new BridgeCommandException("生成的 DirectShape 几何无效，未写入模型。");
            }
            shape.ApplicationId = BridgeProtocol.Version;
            shape.ApplicationDataId = PlanValues.String(step.Arguments, step.Id, "application_data_id");
            shape.Name = name;
            shape.SetShape(geometry);
            data["element_id"] = shape.Id.IntegerValue;
            data["element_ids"] = new[] { shape.Id.IntegerValue };
            return data;
        }

        public static Dictionary<string, object> CreateMepCurve(PlanStep step, PlanExecutionContext context)
        {
            string kind = PlanValues.String(step.Arguments, null, "kind", "system");
            if (string.IsNullOrWhiteSpace(kind))
            {
                throw new BridgeCommandException("create_mep_curve 缺少 kind（pipe、duct、conduit、cable_tray）。");
            }
            XYZ start = PlanValues.Point(step.Arguments, "start");
            XYZ end = PlanValues.Point(step.Arguments, "end");
            if (start.DistanceTo(end) < 1e-8)
            {
                throw new BridgeCommandException("机电曲线 start 与 end 不能重合。");
            }
            Level level = RevitLookups.ResolveLevel(context.Document, step.Arguments);
            string normalizedKind = kind.Trim().ToLowerInvariant();
            var data = new Dictionary<string, object>
            {
                { "kind", normalizedKind },
                { "level", level.Name },
                { "start", PlanValues.PointData(start) },
                { "end", PlanValues.PointData(end) }
            };

            Element created;
            switch (normalizedKind)
            {
                case "pipe":
                    PipeType pipeType = (PipeType)RevitLookups.ResolveElementType(context.Document, typeof(PipeType), step.Arguments, true);
                    PipingSystemType pipingSystem = ResolveSystemType<PipingSystemType>(context.Document, step.Arguments, "system_type");
                    data["type"] = pipeType.Name;
                    data["system_type"] = pipingSystem.Name;
                    if (context.Preview)
                    {
                        AddMepSizeDescription(data, step.Arguments, true, false);
                        return data;
                    }
                    created = Pipe.Create(context.Document, pipingSystem.Id, pipeType.Id, level.Id, start, end);
                    SetMepSize(created, step.Arguments, true, false);
                    break;
                case "duct":
                    DuctType ductType = (DuctType)RevitLookups.ResolveElementType(context.Document, typeof(DuctType), step.Arguments, true);
                    MechanicalSystemType mechanicalSystem = ResolveSystemType<MechanicalSystemType>(context.Document, step.Arguments, "system_type");
                    data["type"] = ductType.Name;
                    data["system_type"] = mechanicalSystem.Name;
                    if (context.Preview)
                    {
                        AddMepSizeDescription(data, step.Arguments, true, true);
                        return data;
                    }
                    created = Duct.Create(context.Document, mechanicalSystem.Id, ductType.Id, level.Id, start, end);
                    SetMepSize(created, step.Arguments, true, true);
                    break;
                case "conduit":
                    ConduitType conduitType = (ConduitType)RevitLookups.ResolveElementType(context.Document, typeof(ConduitType), step.Arguments, true);
                    data["type"] = conduitType.Name;
                    if (context.Preview)
                    {
                        AddMepSizeDescription(data, step.Arguments, true, false);
                        return data;
                    }
                    created = Conduit.Create(context.Document, conduitType.Id, start, end, level.Id);
                    if (PlanValues.Get(step.Arguments, "diameter_mm", "diameter") != null &&
                        !SetOptionalLengthParameter(created, BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM, step.Arguments, "diameter_mm", "diameter"))
                    {
                        throw new BridgeCommandException("创建的线管不支持设置直径参数。");
                    }
                    break;
                case "cable_tray":
                case "cabletray":
                    CableTrayType trayType = (CableTrayType)RevitLookups.ResolveElementType(context.Document, typeof(CableTrayType), step.Arguments, true);
                    data["type"] = trayType.Name;
                    if (context.Preview)
                    {
                        AddMepSizeDescription(data, step.Arguments, false, true);
                        return data;
                    }
                    created = CableTray.Create(context.Document, trayType.Id, start, end, level.Id);
                    if (PlanValues.Get(step.Arguments, "width_mm", "width") != null &&
                        !SetOptionalLengthParameter(created, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, step.Arguments, "width_mm", "width"))
                    {
                        throw new BridgeCommandException("创建的桥架不支持设置宽度参数。");
                    }
                    if (PlanValues.Get(step.Arguments, "height_mm", "height") != null &&
                        !SetOptionalLengthParameter(created, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM, step.Arguments, "height_mm", "height"))
                    {
                        throw new BridgeCommandException("创建的桥架不支持设置高度参数。");
                    }
                    break;
                default:
                    throw new BridgeCommandException("create_mep_curve.kind 仅支持 pipe、duct、conduit、cable_tray。");
            }

            data["element_id"] = created.Id.IntegerValue;
            data["element_ids"] = new[] { created.Id.IntegerValue };
            return data;
        }

        public static Dictionary<string, object> ConnectMep(PlanStep step, PlanExecutionContext context)
        {
            ElementId aId = context.ResolveSingleElementId(step.Arguments, "element_a", "from", "first");
            ElementId bId = context.ResolveSingleElementId(step.Arguments, "element_b", "to", "second");
            string fitting = PlanValues.String(step.Arguments, "auto", "fitting").ToLowerInvariant();
            if (aId.IntegerValue == ElementId.InvalidElementId.IntegerValue ||
                bId.IntegerValue == ElementId.InvalidElementId.IntegerValue)
            {
                return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置元素引用尚无真实 ID。" } };
            }
            Element first = RequireElement(context.Document, aId, "element_a");
            Element second = RequireElement(context.Document, bId, "element_b");
            Connector firstConnector = FindConnector(first, step.Arguments, "connector_a_index", second);
            Connector secondConnector = FindConnector(second, step.Arguments, "connector_b_index", first);
            var data = new Dictionary<string, object>
            {
                { "element_a", aId.IntegerValue },
                { "element_b", bId.IntegerValue },
                { "fitting", fitting },
                { "connector_a_origin", PlanValues.PointData(firstConnector.Origin) },
                { "connector_b_origin", PlanValues.PointData(secondConnector.Origin) }
            };
            if (context.Preview)
            {
                return data;
            }

            Element fittingElement = null;
            switch (fitting)
            {
                case "auto":
                case "direct":
                    firstConnector.ConnectTo(secondConnector);
                    break;
                case "elbow":
                    fittingElement = context.Document.Create.NewElbowFitting(firstConnector, secondConnector);
                    break;
                case "union":
                    fittingElement = context.Document.Create.NewUnionFitting(firstConnector, secondConnector);
                    break;
                case "tee":
                    ElementId cId = context.ResolveSingleElementId(step.Arguments, "element_c", "third");
                    Element third = RequireElement(context.Document, cId, "element_c");
                    Connector thirdConnector = FindConnector(third, step.Arguments, "connector_c_index", first);
                    fittingElement = context.Document.Create.NewTeeFitting(firstConnector, secondConnector, thirdConnector);
                    data["element_c"] = cId.IntegerValue;
                    data["connector_c_origin"] = PlanValues.PointData(thirdConnector.Origin);
                    break;
                default:
                    throw new BridgeCommandException("connect_mep.fitting 仅支持 auto、direct、elbow、union、tee。");
            }
            if (fittingElement != null)
            {
                data["element_id"] = fittingElement.Id.IntegerValue;
                data["element_ids"] = new[] { fittingElement.Id.IntegerValue };
            }
            return data;
        }

        public static Dictionary<string, object> PlaceFamilyInstance(PlanStep step, PlanExecutionContext context)
        {
            FamilySymbol symbol = RevitLookups.ResolveFamilySymbol(context.Document, step.Arguments);
            XYZ point = PlanValues.Point(step.Arguments, "point");
            Level level = RevitLookups.ResolveLevel(context.Document, step.Arguments);
            StructuralType structuralType = ResolveStructuralType(step.Arguments, StructuralType.NonStructural);
            string placementType = symbol.Family == null ? "unknown" : symbol.Family.FamilyPlacementType.ToString();
            EnsurePointPlacementSupported(symbol);
            var data = new Dictionary<string, object>
            {
                { "family", symbol.Family == null ? null : symbol.Family.Name },
                { "type", symbol.Name },
                { "type_id", symbol.Id.IntegerValue },
                { "level", level.Name },
                { "point", PlanValues.PointData(point) },
                { "placement_type", placementType },
                { "structural_type", structuralType.ToString() }
            };
            if (context.Preview)
            {
                return data;
            }

            ActivateSymbol(context.Document, symbol);
            FamilyInstance instance = context.Document.Create.NewFamilyInstance(point, symbol, level, structuralType);
            data["element_id"] = instance.Id.IntegerValue;
            data["element_ids"] = new[] { instance.Id.IntegerValue };
            return data;
        }

        public static Dictionary<string, object> CreateStructuralMember(PlanStep step, PlanExecutionContext context)
        {
            string kind = PlanValues.String(step.Arguments, null, "kind");
            if (string.IsNullOrWhiteSpace(kind))
            {
                throw new BridgeCommandException("create_structural_member 缺少 kind（beam、brace、column）。");
            }
            FamilySymbol symbol = RevitLookups.ResolveFamilySymbol(context.Document, step.Arguments);
            Level level = RevitLookups.ResolveLevel(context.Document, step.Arguments);
            string normalizedKind = kind.Trim().ToLowerInvariant();
            StructuralType structuralType;
            var data = new Dictionary<string, object>
            {
                { "kind", normalizedKind },
                { "family", symbol.Family == null ? null : symbol.Family.Name },
                { "type", symbol.Name },
                { "type_id", symbol.Id.IntegerValue },
                { "level", level.Name }
            };

            if (normalizedKind == "beam" || normalizedKind == "brace")
            {
                XYZ start = PlanValues.Point(step.Arguments, "start");
                XYZ end = PlanValues.Point(step.Arguments, "end");
                if (start.DistanceTo(end) < 1e-8)
                {
                    throw new BridgeCommandException("结构线构件 start 与 end 不能重合。");
                }
                structuralType = normalizedKind == "beam" ? StructuralType.Beam : StructuralType.Brace;
                data["start"] = PlanValues.PointData(start);
                data["end"] = PlanValues.PointData(end);
                if (context.Preview)
                {
                    return data;
                }
                ActivateSymbol(context.Document, symbol);
                FamilyInstance lineInstance = context.Document.Create.NewFamilyInstance(
                    Line.CreateBound(start, end), symbol, level, structuralType);
                data["element_id"] = lineInstance.Id.IntegerValue;
                data["element_ids"] = new[] { lineInstance.Id.IntegerValue };
                return data;
            }

            if (normalizedKind == "column")
            {
                XYZ point = PlanValues.Point(step.Arguments, "point");
                data["point"] = PlanValues.PointData(point);
                if (context.Preview)
                {
                    return data;
                }
                ActivateSymbol(context.Document, symbol);
                FamilyInstance column = context.Document.Create.NewFamilyInstance(point, symbol, level, StructuralType.Column);
                SetOptionalLengthParameter(column, BuiltInParameter.INSTANCE_LENGTH_PARAM, step.Arguments, "height_mm", "height");
                data["element_id"] = column.Id.IntegerValue;
                data["element_ids"] = new[] { column.Id.IntegerValue };
                return data;
            }
            throw new BridgeCommandException("create_structural_member.kind 仅支持 beam、brace、column。");
        }

        private static T ResolveSystemType<T>(Document document, IDictionary<string, object> arguments, string fieldName)
            where T : ElementType
        {
            object idValue = PlanValues.Get(arguments, fieldName + "_id");
            if (idValue != null)
            {
                int id = RevitLookups.ParsePositiveId(idValue, fieldName + "_id");
                T byId = document.GetElement(new ElementId(id)) as T;
                if (byId == null)
                {
                    throw new BridgeCommandException(fieldName + "_id=" + id + " 不是 " + typeof(T).Name + "。");
                }
                return byId;
            }

            string requestedName = PlanValues.String(arguments, null, fieldName);
            List<T> candidates = new FilteredElementCollector(document)
                .OfClass(typeof(T))
                .Cast<T>()
                .OrderBy(RevitLookups.ElementName)
                .ToList();
            if (!string.IsNullOrWhiteSpace(requestedName))
            {
                candidates = candidates.Where(candidate =>
                    string.Equals(candidate.Name, requestedName, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (candidates.Count == 0)
            {
                throw new BridgeCommandException("当前项目没有可用 " + typeof(T).Name + "。先用 query_catalog(kind=mep_types) 查询。");
            }
            return candidates[0];
        }

        private static void AddMepSizeDescription(
            IDictionary<string, object> data,
            IDictionary<string, object> arguments,
            bool allowDiameter,
            bool allowRectangular)
        {
            object diameter = PlanValues.Get(arguments, "diameter_mm", "diameter");
            object width = PlanValues.Get(arguments, "width_mm", "width");
            object height = PlanValues.Get(arguments, "height_mm", "height");
            if (diameter != null)
            {
                if (!allowDiameter)
                {
                    throw new BridgeCommandException("此 MEP 曲线不支持 diameter_mm。");
                }
                data["diameter_mm"] = PlanValues.ParseMillimeters(diameter, "diameter_mm");
            }
            if (width != null || height != null)
            {
                if (!allowRectangular)
                {
                    throw new BridgeCommandException("此 MEP 曲线不支持 width_mm/height_mm。");
                }
                if (width != null)
                {
                    data["width_mm"] = PlanValues.ParseMillimeters(width, "width_mm");
                }
                if (height != null)
                {
                    data["height_mm"] = PlanValues.ParseMillimeters(height, "height_mm");
                }
            }
        }

        private static void SetMepSize(Element element, IDictionary<string, object> arguments, bool allowDiameter, bool allowRectangular)
        {
            AddMepSizeDescription(new Dictionary<string, object>(), arguments, allowDiameter, allowRectangular);
            if (PlanValues.Get(arguments, "diameter_mm", "diameter") != null && allowDiameter)
            {
                bool pipeDiameterSet = SetOptionalLengthParameter(element, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, arguments, "diameter_mm", "diameter");
                bool curveDiameterSet = SetOptionalLengthParameter(element, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, arguments, "diameter_mm", "diameter");
                if (!pipeDiameterSet && !curveDiameterSet)
                {
                    throw new BridgeCommandException("创建的 MEP 曲线不支持设置直径参数。");
                }
            }
            if (allowRectangular)
            {
                if (PlanValues.Get(arguments, "width_mm", "width") != null &&
                    !SetOptionalLengthParameter(element, BuiltInParameter.RBS_CURVE_WIDTH_PARAM, arguments, "width_mm", "width"))
                {
                    throw new BridgeCommandException("创建的 MEP 曲线不支持设置宽度参数。");
                }
                if (PlanValues.Get(arguments, "height_mm", "height") != null &&
                    !SetOptionalLengthParameter(element, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, arguments, "height_mm", "height"))
                {
                    throw new BridgeCommandException("创建的 MEP 曲线不支持设置高度参数。");
                }
            }
        }

        private static bool SetOptionalLengthParameter(
            Element element,
            BuiltInParameter builtInParameter,
            IDictionary<string, object> arguments,
            params string[] fieldNames)
        {
            object raw = PlanValues.Get(arguments, fieldNames);
            if (raw == null)
            {
                return false;
            }
            double millimeters = PlanValues.ParseMillimeters(raw, string.Join("/", fieldNames));
            if (millimeters <= 0.0)
            {
                throw new BridgeCommandException("参数 " + string.Join("/", fieldNames) + " 必须大于 0。");
            }
            Parameter parameter = element.get_Parameter(builtInParameter);
            if (parameter == null || parameter.IsReadOnly)
            {
                return false;
            }
            parameter.Set(PlanValues.ToFeet(millimeters));
            return true;
        }

        private static Element RequireElement(Document document, ElementId id, string fieldName)
        {
            Element element = document.GetElement(id);
            if (element == null)
            {
                throw new BridgeCommandException("找不到 " + fieldName + "=" + id.IntegerValue + " 对应元素。");
            }
            return element;
        }

        private static Connector FindConnector(
            Element element,
            IDictionary<string, object> arguments,
            string indexName,
            Element otherElement)
        {
            List<Connector> connectors = GetConnectors(element);
            if (connectors.Count == 0)
            {
                throw new BridgeCommandException("元素 " + element.Id.IntegerValue + " 没有可连接的 MEP Connector。");
            }
            object rawIndex = PlanValues.Get(arguments, indexName);
            if (rawIndex != null)
            {
                int index = PlanValues.Integer(arguments, -1, indexName);
                connectors = connectors.OrderBy(connector => connector.Origin.X)
                    .ThenBy(connector => connector.Origin.Y)
                    .ThenBy(connector => connector.Origin.Z)
                    .ToList();
                if (index < 0 || index >= connectors.Count)
                {
                    throw new BridgeCommandException(indexName + " 超出范围 0-" + (connectors.Count - 1) + "。");
                }
                return connectors[index];
            }

            List<Connector> otherConnectors = GetConnectors(otherElement);
            if (otherConnectors.Count == 0)
            {
                return connectors[0];
            }
            return connectors.OrderBy(connector => otherConnectors.Min(other => connector.Origin.DistanceTo(other.Origin))).First();
        }

        private static List<Connector> GetConnectors(Element element)
        {
            ConnectorManager manager = null;
            MEPCurve curve = element as MEPCurve;
            if (curve != null)
            {
                manager = curve.ConnectorManager;
            }
            else
            {
                FamilyInstance instance = element as FamilyInstance;
                if (instance != null && instance.MEPModel != null)
                {
                    manager = instance.MEPModel.ConnectorManager;
                }
            }
            if (manager == null)
            {
                return new List<Connector>();
            }
            var result = new List<Connector>();
            foreach (Connector connector in manager.Connectors)
            {
                if (connector.ConnectorType == ConnectorType.End)
                {
                    result.Add(connector);
                }
            }
            return result;
        }

        private static StructuralType ResolveStructuralType(IDictionary<string, object> arguments, StructuralType defaultValue)
        {
            string requested = PlanValues.String(arguments, null, "structural_type");
            if (string.IsNullOrWhiteSpace(requested))
            {
                return defaultValue;
            }
            StructuralType parsed;
            if (!Enum.TryParse(requested, true, out parsed))
            {
                throw new BridgeCommandException("structural_type 无效：" + requested);
            }
            return parsed;
        }

        private static void EnsurePointPlacementSupported(FamilySymbol symbol)
        {
            if (symbol.Family == null)
            {
                throw new BridgeCommandException("族类型缺少 Family 信息。");
            }
            FamilyPlacementType placementType = symbol.Family.FamilyPlacementType;
            if (placementType != FamilyPlacementType.OneLevelBased &&
                placementType != FamilyPlacementType.TwoLevelsBased)
            {
                throw new BridgeCommandException(
                    "族“" + symbol.Family.Name + "”的放置类型为 " + placementType + "；当前 place_family_instance 仅支持 OneLevelBased 和 TwoLevelsBased 非宿主族。");
            }
        }

        private static void ActivateSymbol(Document document, FamilySymbol symbol)
        {
            if (!symbol.IsActive)
            {
                symbol.Activate();
                document.Regenerate();
            }
        }

        private static WallType ResolveOrCreateWallType(Document document, WallType sourceType, string targetName, double thicknessMm)
        {
            WallType existing = new FilteredElementCollector(document)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .FirstOrDefault(type => type.Kind == WallKind.Basic &&
                    string.Equals(type.Name, targetName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (Math.Abs(PlanValues.ToMillimeters(existing.Width) - thicknessMm) > 0.5)
                {
                    throw new BridgeCommandException("墙类型“" + targetName + "”已存在，且厚度不匹配；不会改动既有类型。");
                }
                return existing;
            }

            WallType duplicate = sourceType.Duplicate(targetName) as WallType;
            if (duplicate == null)
            {
                throw new BridgeCommandException("无法复制墙类型“" + sourceType.Name + "”。");
            }
            CompoundStructure structure = duplicate.GetCompoundStructure();
            if (structure == null || structure.LayerCount < 1)
            {
                throw new BridgeCommandException("墙类型“" + sourceType.Name + "”没有可调整的复合结构。");
            }
            double targetTotal = PlanValues.ToFeet(thicknessMm);
            double currentTotal = structure.GetLayers().Sum(layer => layer.Width);
            int index = structure.VariableLayerIndex >= 0 ? structure.VariableLayerIndex : 0;
            double adjustedWidth = structure.GetLayers()[index].Width + targetTotal - currentTotal;
            if (adjustedWidth <= 0.0)
            {
                throw new BridgeCommandException("指定墙厚会使墙类型“" + sourceType.Name + "”的层厚无效。");
            }
            structure.SetLayerWidth(index, adjustedWidth);
            duplicate.SetCompoundStructure(structure);
            return duplicate;
        }
    }
}
