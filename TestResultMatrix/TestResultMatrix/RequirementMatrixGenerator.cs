﻿using Microsoft.TeamFoundation.Client;
using Microsoft.TeamFoundation.TestManagement.Client;
using Microsoft.TeamFoundation.WorkItemTracking.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TestResultMatrix
{
    public class RequirementMatrixGenerator
    {
        public string TpcUrl { get; private set; }

        public string ProjectName { get; private set; }

        public string RequirementsQuery { get; private set; }

        public RequirementMatrix Matrix { get; private set; }

        public RequirementMatrixGenerator(string tpcUrl, string projectName, string requirementsQuery = null)
        {
            TpcUrl = tpcUrl;
            ProjectName = projectName;
            RequirementsQuery = requirementsQuery;
        }

        public void Process()
        {
            var tpc = TfsTeamProjectCollectionFactory.GetTeamProjectCollection(new Uri(TpcUrl));
            var store = tpc.GetService<WorkItemStore>();
            var testSvc = tpc.GetService<ITestManagementService>();

            Matrix = new RequirementMatrix();

            var wiql = string.Format(
                       @"SELECT [System.Id]
                         FROM WorkItemLinks
                         WHERE
                           Source.[System.TeamProject] = @project AND {0}
                           Source.[System.WorkItemType] IN GROUP 'Microsoft.RequirementCategory' AND
                           Target.[System.WorkItemType] IN GROUP 'Microsoft.TestCaseCategory'
                         MODE(MAYCONTAIN)", GetIDClause(store));

            var hQyery = new Query(store, wiql, new Dictionary<string, object>() { { "project", ProjectName } });
            var links = hQyery.RunLinkQuery();

            // add items with children
            Matrix.Requirements = (from link in links
                                   where link.SourceId > 0
                                   group link by link.SourceId into reqs
                                   select new Requirement
                                   {
                                       Id = reqs.Key,
                                       TestIds = reqs.Select(t => t.TargetId).ToList()
                                   }).ToList();

            // add items without children
            var childLess = from link in links
                            where link.SourceId == 0 && !Matrix.Requirements.Any(t => t.Id == link.TargetId)
                            select new Requirement
                            {
                                Id = link.TargetId
                            };
            Matrix.Requirements.AddRange(childLess);

            var testIds = new List<int>();
            Matrix.Requirements.ForEach(t => testIds = testIds.Union(t.TestIds).ToList());
            Matrix.Tests = testIds.ConvertAll(i => new WorkItemInfo() { Id = i });

            var plans = testSvc.GetTeamProject(ProjectName).TestPlans.Query("SELECT * FROM TestPlan");

            foreach (var testId in testIds)
            {
                var mostRecent = GetLatestTestResult(testId, plans);
                Matrix.Tests.First(t => t.Id == testId).MatrixState = mostRecent;
            }

            Matrix.Requirements.ForEach(r => GetItemInfo(r, store));
            Matrix.Tests.ForEach(t => GetItemInfo(t, store, false));
        }

        private string GetIDClause(WorkItemStore store)
        {
            if (!string.IsNullOrEmpty(RequirementsQuery))
            {
                var query = FindQueryRecursive(store.Projects[ProjectName].QueryHierarchy);
                if (query == null)
                {
                    throw new ApplicationException(string.Format("Could not find query [{0}]", RequirementsQuery));
                }
                if (query.QueryType != QueryType.List)
                {
                    throw new ApplicationException(string.Format("Query [{0}] is not a flat-list query - only flat-list queries are supported", RequirementsQuery));
                }

                var reqs = store.Query(query.QueryText, new Dictionary<string, object> { { "project", ProjectName } } );
                var ids = reqs.Cast<WorkItem>().Select(w => w.Id.ToString()).Aggregate("", (w, s) => s + "," + w);
                ids = ids.Substring(0, ids.Length - 1);

                return string.Format("Source.[System.ID] IN ({0}) AND", ids);
            }
            return "";
        }

        private QueryDefinition FindQueryRecursive(QueryFolder folder)
        {
            var query = folder.OfType<QueryDefinition>().FirstOrDefault(q => q.Name == RequirementsQuery);
            if (query != null)
            {
                return query;
            }
            else
            {
                foreach (var subFolder in folder.OfType<QueryFolder>())
                {
                    query = FindQueryRecursive(subFolder);
                    if (query != null)
                    {
                        return query;
                    }
                }
            }

            return null;
        }

        private void GetItemInfo(WorkItemInfo item, WorkItemStore store, bool isRequirement = true)
        {
            var workItem = store.GetWorkItem(item.Id);
            item.Title = workItem.Title;
            if (isRequirement)
            {
                item.MatrixState = workItem.State;
            }
        }

        private string GetLatestTestResult(int testId, ITestPlanCollection plans)
        {
            //System.Diagnostics.Debug.Print("----------------------------------------------");
            //System.Diagnostics.Debug.Print(string.Format("Test case Id:{0}", testId));
            ITestPoint mostRecentPoint = null;
            foreach (ITestPlan plan in plans)
            {
                var points = plan.QueryTestPoints(string.Format("SELECT * FROM TestPoint WHERE TestCaseId = {0}", testId));
                //System.Diagnostics.Debug.Print(string.Format("Test Plan Id:{0} Name:{1} Iteration:{2} ResultPoints:{3}", plan.Id, plan.Name, plan.Iteration, points.Count.ToString()));
                foreach (ITestPoint point in points)
                {
                    //if (mostRecentPoint != null) System.Diagnostics.Debug.Print(string.Format("Current most recent Point last updated:{0}", mostRecentPoint.LastUpdated.ToString("dd/MMM/yyyy HH:mm:ss")));
                    //System.Diagnostics.Debug.Print(string.Format("Test Point last updated:{0}", point.LastUpdated.ToString("dd/MMM/yyyy HH:mm:ss")));
                    //if (mostRecentPoint == null || mostRecentPoint.LastUpdated < point.LastUpdated)
                    if (mostRecentPoint == null ||
                        mostRecentPoint.MostRecentResult == null ||
                        (mostRecentPoint.LastUpdated < point.LastUpdated && point.MostRecentResult != null)
                        )
                    {
                        mostRecentPoint = point;
                        //System.Diagnostics.Debug.Print(string.Format("SET - Most Recent Point to last updated:{0} Test Plan Id:{1} Name:{2} Iteration:{3} ", point.LastUpdated.ToString("dd/MMM/yyyy HH:mm:ss"), plan.Id, plan.Name, plan.Iteration));
                    }
                }
            }

            if (mostRecentPoint == null)
            {
                return "N/A";
            }

            var mostRecent = "Not run";
            if (mostRecentPoint.MostRecentResult != null)
            {
                mostRecent = mostRecentPoint.MostRecentResult.Outcome.ToString();
                //System.Diagnostics.Debug.Print(string.Format("Most Recent outcome set:{0}", mostRecent));
            }
            //System.Diagnostics.Debug.Print(string.Format("Most Recent outcome returning:{0}", mostRecent));
            return mostRecent;
        }
    }
}
