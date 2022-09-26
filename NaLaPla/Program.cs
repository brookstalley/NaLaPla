﻿namespace NaLaPla
{
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Configuration.EnvironmentVariables;

    enum ExpandModeType
    {
        ONE_BY_ONE,
        AS_A_LIST    
    }

    class Program {
    
        static Task ?basePlan;
        static int GPTRequestsTotal = 0;
        static int GPTRequestsInFlight = 0;

        static ExpandModeType ExpandMode = ExpandModeType.AS_A_LIST;
        const int ExpandDepth = 2;
        const string ExpandSubtaskCount = "four";
        const bool shouldWriteOutputFile = true;
        const bool parallelGPTRequests = true;
        const bool showPrompts = false; // whether or not to print each prompt as it is submitted to GPT. Prompts always stored in plan.prompt.

        static List<string> PostProcessingPrompts = new List<string>() {
            "Repeat the task list below removing any steps that are redundant"
        };

        static async System.Threading.Tasks.Task Main(string[] args)
        {
            var root = Directory.GetCurrentDirectory();
            var dotenv = Path.Combine(root, ".env");
            DotEnv.Load(dotenv);

            var config =
                new ConfigurationBuilder()
                    .AddEnvironmentVariables()
                    .Build();

            //Util.TestParseMultiList();
            //return;
            var configList = $"ExpandDepth = {ExpandDepth}, ExpandSubtaskCount = {ExpandSubtaskCount}, shouldWriteOutputFile = {shouldWriteOutputFile}, "
                + $"parallelGPTRequests = {parallelGPTRequests}, showPrompts = {showPrompts}";
            Util.WriteToConsole($"\n\n\n{configList}", ConsoleColor.Green);
            Console.WriteLine($"What do you want to plan?");
            String plan = Console.ReadLine();

            basePlan = new Task() {
                description = plan,
                planLevel = 0, 
                subTasks = new List<Task>()
                };

            await ExpandPlan(basePlan);
            Util.WriteResults(basePlan, configList, shouldWriteOutputFile);
        }

       static async System.Threading.Tasks.Task ExpandPlan(Task planToExpand) {

            if (planToExpand.planLevel > ExpandDepth) {
                planToExpand.state = "final";
                return;
            }
            planToExpand.state = "calling GPT";
            Util.DisplayProgress(basePlan,GPTRequestsInFlight);
            var gptResponse = await GetGPTResponse(planToExpand, showPrompts);
            planToExpand.state = "processing";
            // If one sub item at a time, create children and then expand with
            if (ExpandMode == ExpandModeType.ONE_BY_ONE) {
                
                planToExpand.subTaskDescriptions = Util.ParseSubTaskList(gptResponse);

                // Create sub-tasks
                foreach (var subTask in planToExpand.subTaskDescriptions) {
                    var subPlan = new Task() {
                        description = subTask,
                        planLevel = planToExpand.planLevel + 1,
                        subTasks = new List<Task>(),
                        parent = planToExpand
                    };
                    planToExpand.subTasks.Add(subPlan);
                }
                if (parallelGPTRequests) {
                    var tasks = planToExpand.subTasks.Select(async subTask =>
                    {
                            await ExpandPlan(subTask);
                    });
                    await System.Threading.Tasks.Task.WhenAll(tasks);
                } else {
                    foreach (var subPlan in planToExpand.subTasks) {
                        await ExpandPlan(subPlan);
                    }
                }
            }
            // Otherwise, expand all at once and then create children
            else if (ExpandMode == ExpandModeType.AS_A_LIST) {

                if (planToExpand.subTaskDescriptions.Count == 0) {
                    planToExpand.subTaskDescriptions = Util.ParseSubTaskList(gptResponse);
                    await ExpandPlan(planToExpand);
                }
                else {
                    // Only request a display if we're not using parallel requests
                    Util.UpdatePlan(planToExpand, gptResponse, !parallelGPTRequests);

                    // If I haven't reached the end of the plan
                    if (planToExpand.subTasks.Count > 0 ) {
                        if (parallelGPTRequests) {
                            var tasks = planToExpand.subTasks.Select(async subTask =>
                            {
                                if (subTask.subTaskDescriptions.Any()) {
                                    await ExpandPlan(subTask);
                                }
                            });
                            await System.Threading.Tasks.Task.WhenAll(tasks);
                        } else {
                            foreach (var subPlan in planToExpand.subTasks) {
                                if (subPlan.subTaskDescriptions.Any()) {
                                    await ExpandPlan(subPlan);
                                }
                            }
                        }
                    }
                }
            }
            planToExpand.state ="done";
            Util.DisplayProgress(basePlan,GPTRequestsInFlight);
        }

        static async Task<string> GetGPTResponse(Task plan, bool showPrompt) {
            var prompt = GeneratePrompt(plan,showPrompt);
            var GPTresponse = await GetGPTResponse(prompt);
            plan.GPTresponse = GPTresponse;
            return GPTresponse;
        }

        static async Task<string> GetGPTResponse(string prompt) {
            var apiKey = System.Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            var api = new OpenAI_API.OpenAIAPI(apiKey, "text-davinci-002");
            GPTRequestsInFlight++;
            OpenAI_API.CompletionResult result = await api.Completions.CreateCompletionAsync(
                prompt,
                max_tokens: 500,
                temperature: 0.2);
            GPTRequestsInFlight--;
            var rawPlan = result.ToString();
            GPTRequestsTotal++;
            return rawPlan;
        }

        static string GeneratePrompt(Task plan, bool showPrompt) {
            if (plan.planLevel > 0  && ExpandMode == ExpandModeType.ONE_BY_ONE) {
                // var prompt =  $"Your job is to {plan.parent.description}. Your current task is to {plan.description}. Please specify a numbered list of the work that needs to be done.";
                //var prompt = $"Please specify a numbered list of the work that needs to be done to {plan.description} when you {basePlan.description}";
                //var prompt = $"Please specify one or two steps that needs to be done to {plan.description} when you {basePlan.description}";
                var prompt = $"Your task is to {basePlan.description}. Repeat the list and add {ExpandSubtaskCount} subtasks to each of the items.\n\n";
                prompt += Util.GetNumberedSteps(plan);
                prompt += "END LIST";
                plan.prompt = prompt;
                if (showPrompt) {
                    Util.WriteToConsole($"\n{prompt}\n", ConsoleColor.Cyan);
                }
                return prompt;
            }
            else if (plan.subTaskDescriptions.Count > 0 && ExpandMode == ExpandModeType.AS_A_LIST) {
                /*
                var prompt =  $"Your job is to {plan.description}. You have identified the following steps:\n";
                prompt += Util.GetNumberedSteps(plan);
                prompt += "Please specify a bulleted list of the work that needs to be done for each step.";
                */
                var prompt = $"Below is part of a plan to {basePlan.description}. Repeat the list and add {ExpandSubtaskCount} subtasks to each of the items\n\n";
                prompt += Util.GetNumberedSteps(plan);
                prompt += "END LIST";
                plan.prompt = prompt;
                if (showPrompt) {
                    Util.WriteToConsole($"\n{prompt}\n", ConsoleColor.Cyan);
                }
                return prompt;
            }
            var firstPrompt =  $"Your job is to {plan.description}. Please specify a numbered list of brief tasks that needs to be done.";
            plan.prompt = firstPrompt;
            if (showPrompt) {
                Util.WriteToConsole($"\n{firstPrompt}\n", ConsoleColor.Cyan);
            }
            return firstPrompt;
        }
    }
  }