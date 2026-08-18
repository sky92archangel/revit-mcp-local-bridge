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
                case "create_level":
                    return RevitPlanCreations.CreateLevel(step, context);
                case "create_grid":
                    return RevitPlanCreations.CreateGrid(step, context);
                case "create_wall":
                    return RevitPlanCreations.CreateWall(step, context);
                case "create_direct_shape":
                    return RevitPlanCreations.CreateDirectShape(step, context);
                case "create_mep_curve":
                    return RevitPlanCreations.CreateMepCurve(step, context);
                case "connect_mep":
                    return RevitPlanCreations.ConnectMep(step, context);
                case "place_family_instance":
                    return RevitPlanCreations.PlaceFamilyInstance(step, context);
                case "create_structural_member":
                    return RevitPlanCreations.CreateStructuralMember(step, context);
                case "set_parameters":
                    return RevitPlanMutations.SetParameters(step, context);
                case "delete_elements":
                    return RevitPlanMutations.DeleteElements(step, context);
                case "select_elements":
                    return RevitPlanMutations.SelectElements(step, context);
                default:
                    throw new BridgeCommandException("未注册计划原子操作：" + step.Operation);
            }
        }
    }
}
