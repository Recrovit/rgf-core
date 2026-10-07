using Recrovit.AI.Core.Workflows;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby.Workflows;

public static class RgfRecrobyWorkflowConfigurationExtensions
{
    /// <summary>Optional default chat workflow. Applications can register and select their own workflow instead.</summary>
    public static void AddDefaultRecrobyWorkflow(this RecrovitWorkflowConfiguration configuration)
    {
        configuration.Contracts.Register<string>("rgf.recroby.response");
        configuration.Workflows.Register(new WorkflowDefinition
        {
            Id = "rgf.recroby", Name = "Recroby", EntryAgentId = "rgf.recroby.chat",
            Agents = [new AgentDefinition
            {
                Id = "rgf.recroby.chat", Name = "Chat", Type = AgentType.Executor,
                ExecutorExecutionMode = ExecutorExecutionMode.AI,
                Instruction = "Respond to the user's message.", IncludeConversationHistory = true,
                AllowInputRequest = false, OutputContractId = "rgf.recroby.response"
            }]
        });
    }
}
