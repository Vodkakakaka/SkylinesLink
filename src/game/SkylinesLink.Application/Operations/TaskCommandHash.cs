using System;
using SkylinesLink.Application.Context;
using SkylinesLink.Shared.Tasks;

namespace SkylinesLink.Application.Operations
{
    /// <summary>仅规范化客户端意图；生成taskId、请求key及actor/epoch审计不混作重放策略。</summary>
    public static class TaskCommandHash
    {
        public static string Create(WorldContextSnapshot source, TaskStatedGoal goal)
            => TaskJournalRules.ComputeCreateHash(SourceView(source), GoalView(goal));
        public static string Revise(string taskId, long expectedGoalRevision, long expectedControlVersion, WorldContextSnapshot source, TaskStatedGoal goal)
        {
            return TaskJournalRules.ComputeReviseHash(taskId, expectedGoalRevision, expectedControlVersion, SourceView(source), GoalView(goal));
        }
        public static string Stop(string taskId, long expectedControlVersion)
        {
            return TaskJournalRules.ComputeStopHash(taskId, expectedControlVersion);
        }
        // 稳定请求身份，不授予权限；不依赖短期context、epoch、世界或可变goal。
        public static SubmissionKey StopKey(string taskId, long expectedControlVersion) =>
            new SubmissionKey("task-control." + Stop(taskId, expectedControlVersion), "stop");
        internal static TaskSourceView SourceView(WorldContextSnapshot source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            return new TaskSourceView(source.InstanceId, source.Generation, source.State, source.CityId);
        }
        internal static TaskGoalView GoalView(TaskStatedGoal goal)
        {
            if (goal == null) throw new ArgumentNullException(nameof(goal));
            return new TaskGoalView(goal.Title, goal.RequestText, goal.ConstraintsText, goal.AcceptanceText);
        }
    }
}
