using System.Collections.Generic;

namespace RevitCommandBridge
{
    internal static class RevitPlanOperations
    {
        public static Dictionary<string, object> Execute(PlanStep step, PlanExecutionContext context)
        {
            switch (step.Operation)
            {
                case "query_document":
                    return RevitPlanQueries.QueryDocument(context);
                case "query_catalog":
                    return RevitPlanQueries.QueryCatalog(step, context);
                case "query_elements":
                    return RevitPlanQueries.QueryElements(step, context);
                case "query_references":
                    return RevitPlanQueries.QueryReferences(step, context);
                case "query_parameters":
                    return RevitPlanQueries.QueryParameters(step, context);
                case "query_geometry":
                    return RevitPlanQueries.QueryGeometry(step, context);
                case "query_room":
                    return RevitPlanQueries.QueryRoom(step, context);
                case "check_interferences":
                    return RevitPlanQueries.CheckInterferences(step, context);
                case "create_level":
                    return RevitPlanCreations.CreateLevel(step, context);
                case "create_grid":
                    return RevitPlanCreations.CreateGrid(step, context);
                case "create_wall":
                    return RevitPlanCreations.CreateWall(step, context);
                case "create_floor":
                    return RevitPlanCreations.CreateFloor(step, context);
                case "create_room":
                    return RevitPlanCreations.CreateRoom(step, context);
                case "create_space":
                    return RevitPlanCreations.CreateSpace(step, context);
                case "create_model_curve":
                    return RevitPlanCreations.CreateModelCurve(step, context);
                case "create_direct_shape":
                    return RevitPlanCreations.CreateDirectShape(step, context);
                case "create_mep_curve":
                    return RevitPlanCreations.CreateMepCurve(step, context);
                case "connect_mep":
                    return RevitPlanCreations.ConnectMep(step, context);
                case "create_mep_system":
                    return RevitPlanCreations.CreateMepSystem(step, context);
                case "place_family_instance":
                    return RevitPlanCreations.PlaceFamilyInstance(step, context);
                case "load_family":
                    return RevitPlanCreations.LoadFamily(step, context);
                case "create_structural_member":
                    return RevitPlanCreations.CreateStructuralMember(step, context);
                case "create_view":
                    return RevitPlanCreations.CreateView(step, context);
                case "create_drafting_view":
                    return RevitOutputOperations.CreateDraftingView(step, context);
                case "create_section_view":
                    return RevitOutputOperations.CreateSectionView(step, context);
                case "create_elevation_view":
                    return RevitOutputOperations.CreateElevationView(step, context);
                case "create_callout":
                    return RevitOutputOperations.CreateCallout(step, context);
                case "duplicate_view":
                    return RevitOutputOperations.DuplicateView(step, context);
                case "create_view_template":
                    return RevitOutputOperations.CreateViewTemplate(step, context);
                case "create_sheet":
                    return RevitPlanCreations.CreateSheet(step, context);
                case "place_view_on_sheet":
                    return RevitPlanCreations.PlaceViewOnSheet(step, context);
                case "create_detail_curve":
                    return RevitOutputOperations.CreateDetailCurve(step, context);
                case "create_text_note":
                    return RevitOutputOperations.CreateTextNote(step, context);
                case "create_dimension":
                    return RevitOutputOperations.CreateDimension(step, context);
                case "create_tag":
                    return RevitOutputOperations.CreateTag(step, context);
                case "create_filled_region":
                    return RevitOutputOperations.CreateFilledRegion(step, context);
                case "create_revision":
                    return RevitOutputOperations.CreateRevision(step, context);
                case "create_revision_cloud":
                    return RevitOutputOperations.CreateRevisionCloud(step, context);
                case "create_schedule":
                    return RevitOutputOperations.CreateSchedule(step, context);
                case "place_schedule_on_sheet":
                    return RevitOutputOperations.PlaceScheduleOnSheet(step, context);
                case "set_view_properties":
                    return RevitOutputOperations.SetViewProperties(step, context);
                case "create_opening":
                    return RevitPlanCreations.CreateOpening(step, context);
                case "set_parameters":
                    return RevitPlanMutations.SetParameters(step, context);
                case "transform_elements":
                    return RevitPlanMutations.TransformElements(step, context);
                case "rename_element":
                    return RevitPlanMutations.RenameElement(step, context);
                case "set_element_curve":
                    return RevitPlanMutations.SetElementCurve(step, context);
                case "delete_elements":
                    return RevitPlanMutations.DeleteElements(step, context);
                case "select_elements":
                    return RevitPlanMutations.SelectElements(step, context);
                case "export":
                    return RevitOutputOperations.Export(step, context);
                case "save_document":
                    return RevitOutputOperations.SaveDocument(step, context);
                default:
                    throw new BridgeCommandException("未注册计划原子操作：" + step.Operation);
            }
        }
    }
}
