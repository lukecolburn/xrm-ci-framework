﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Text;
using System.Threading.Tasks;
using Xrm.Framework.CI.PowerShell.Cmdlets.Common;
using Xrm.Framework.CI.PowerShell.Cmdlets.PluginRegistration;

namespace Xrm.Framework.CI.PowerShell.Cmdlets
{
    /// <summary>
    /// <para type="synopsis">Plugin Registration.</para>
    /// <para type="description">The Set-XrmPluginRegistration cmdlet updates an existing Plugin Assembly and steps in CRM.
    /// </summary>
    /// <example>
    ///   <code>C:\PS>Set-XrmPluginRegistration -AssemblyPath $path -MappingJsonPath $jsonPath</code>
    ///   <para>Updates a Plugin Assembly and Steps.</para>
    /// </example>
    /// <para type="link" uri="http://msdn.microsoft.com/en-us/library/microsoft.xrm.sdk.messages.updaterequest.aspx">UpdateRequest.</para>
    [Cmdlet(VerbsCommon.Set, "XrmPluginRegistration")]
    public class SetXrmPluginRegistration : XrmCommandBase
    {
        #region Parameters

        [Parameter(Mandatory = true)]
        public String RegistrationType { get; set; }

        /// <summary>
        /// <para type="description">The full path to the assembly. e.g. C:\Solution\bin\release\Plugin.dll</para>
        /// </summary>
        [Parameter(Mandatory = true)]
        public String AssemblyPath { get; set; }

        [Parameter(Mandatory = false)]
        public bool UseSplitAssembly { get; set; }

        [Parameter(Mandatory = false)]
        public string ProjectFilePath { get; set; }

        [Parameter(Mandatory = true)]
        public Boolean IsWorkflowActivityAssembly { get; set; }

        [Parameter(Mandatory = false)]
        public String MappingJsonPath { get; set; }

        [Parameter(Mandatory = false)]
        public String SolutionName { get; set; }
        #endregion

        #region Process Record

        protected override void ProcessRecord()
        {
            base.ProcessRecord();

            base.WriteVerbose("Plugin Registration intiated");

            if (UseSplitAssembly)
            {
                if (!File.Exists(ProjectFilePath)) throw new Exception("Project File Path is required if you want to split assembly.");
                if (RegistrationType.Equals("delsert", StringComparison.InvariantCultureIgnoreCase)) throw new Exception("Registration type 'Remove Plugin Types and Steps which are not in mapping and Upsert' will not work when 'Split Assembly' is enabled.");
                if (!File.Exists(MappingJsonPath)) throw new Exception("Mapping Json Path is required if you want to split assembly.");
            }
            
            var assemblyDetails = AssemblyInfo.GetAssemblyInfo(AssemblyPath); 
            string assemblyName = assemblyDetails.AssemblyName;
            string version = assemblyDetails.Version;
            string content = assemblyDetails.Content;

            base.WriteVerbose(string.Format("Assembly Name: {0}", assemblyName));
            base.WriteVerbose(string.Format("Assembly Version: {0}", version));

            using (var context = new CIContext(OrganizationService))
            {
                PluginRegistrationHelper pluginRegistrationHelper = new PluginRegistrationHelper(OrganizationService, context, this);
                base.WriteVerbose("PluginRegistrationHelper intiated");
                Assembly pluginAssembly = null;
                Guid pluginAssemblyId = Guid.Empty;
                if (File.Exists(MappingJsonPath))
                {
                    base.WriteVerbose("Reading mapping json file");
                    string json = File.ReadAllText(MappingJsonPath);
                    pluginAssembly = JsonConvert.DeserializeObject<Assembly>(json);
                    base.WriteVerbose("Deserialized mapping json file");
                }
                else
                {
                    pluginAssemblyId = pluginRegistrationHelper.UpsertPluginAssembly(pluginAssembly, assemblyName, version, content, SolutionName, IsWorkflowActivityAssembly, RegistrationType);
                    base.WriteVerbose(string.Format("UpsertPluginAssembly {0} completed", pluginAssemblyId));
                }

                if (pluginAssembly != null)
                {
                    // var assemblyTypes = IsWorkflowActivityAssembly ? pluginAssembly.WorkflowTypes : pluginAssembly.PluginTypes;
                    if (pluginAssembly.PluginTypes == null)
                    {
                        base.WriteVerbose("No mapping found for types.");
                    }
                    else
                    {
                        if (RegistrationType.Equals("delsert", StringComparison.InvariantCultureIgnoreCase))
                        {
                            pluginRegistrationHelper.RemoveComponentsNotInMapping(assemblyName, pluginAssembly);
                            RegistrationType = "upsert";
                        }

                        if (!UseSplitAssembly)
                        {
                            pluginAssemblyId = pluginRegistrationHelper.UpsertPluginAssembly(pluginAssembly, assemblyName, version, content, SolutionName, IsWorkflowActivityAssembly, RegistrationType);
                            base.WriteVerbose(string.Format("UpsertPluginAssembly {0} completed", pluginAssemblyId));
                        }

                        foreach (var type in pluginAssembly.PluginTypes)
                        {
                            if (UseSplitAssembly)
                            {
                                pluginAssemblyId = UploadSplitAssembly(assemblyDetails, assemblyName, version, content, pluginRegistrationHelper, pluginAssembly, type);
                            }

                            var pluginTypeId = pluginRegistrationHelper.UpsertPluginType(pluginAssemblyId, type, SolutionName, RegistrationType, IsWorkflowActivityAssembly, assemblyName);
                            base.WriteVerbose(string.Format("UpsertPluginType {0} completed", pluginTypeId));
                            if (!IsWorkflowActivityAssembly)
                            {
                                foreach (var step in type.Steps)
                                {
                                    var sdkMessageProcessingStepId = pluginRegistrationHelper.UpsertSdkMessageProcessingStep(pluginTypeId, step, SolutionName, RegistrationType);
                                    base.WriteVerbose(string.Format("UpsertSdkMessageProcessingStep {0} completed", sdkMessageProcessingStepId));
                                    foreach (var image in step.Images)
                                    {
                                        var sdkMessageProcessingStepImageId = pluginRegistrationHelper.UpsertSdkMessageProcessingStepImage(sdkMessageProcessingStepId, image, SolutionName, RegistrationType);
                                        base.WriteVerbose(string.Format("UpsertSdkMessageProcessingStepImage {0} completed", sdkMessageProcessingStepImageId));
                                    }
                                }
                            }
                        }
                    }
                }
            }

            base.WriteVerbose("Plugin Registration completed");
        }

        private Guid UploadSplitAssembly(AssemblyInfo assemblyDetails, string assemblyName, string version, string content, PluginRegistrationHelper pluginRegistrationHelper, Assembly pluginAssembly, Type type)
        {
            var temp = new FileInfo(ProjectFilePath);
            var splitAssembly = AssemblyInfo.GetAssemblyInfo(assemblyDetails.AssemblyDirectory.Replace(temp.DirectoryName, temp.DirectoryName + type.Name) + "\\" + type.Name + ".dll");
            assemblyName = splitAssembly.AssemblyName;
            version = splitAssembly.Version;
            content = splitAssembly.Content;
            var pluginAssemblyId = pluginRegistrationHelper.UpsertPluginAssembly(pluginAssembly, assemblyName, version, content, SolutionName, IsWorkflowActivityAssembly, RegistrationType);
            base.WriteVerbose(string.Format("UpsertPluginAssembly {0} completed", pluginAssemblyId));
            return pluginAssemblyId;
        }

        #endregion
    }
}