// <auto-generated>
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for
// license information.
//
// Code generated by Microsoft (R) AutoRest Code Generator.
// Changes may cause incorrect behavior and will be lost if the code is
// regenerated.
// </auto-generated>

namespace Microsoft.Azure.Management.ManagedNetwork.Models
{
    using Microsoft.Rest;
    using Microsoft.Rest.Serialization;
    using Newtonsoft.Json;
    using System.Linq;

    /// <summary>
    /// The Managed Network resource
    /// </summary>
    [Rest.Serialization.JsonTransformation]
    public partial class ScopeAssignment : ProxyResource
    {
        /// <summary>
        /// Initializes a new instance of the ScopeAssignment class.
        /// </summary>
        public ScopeAssignment()
        {
            CustomInit();
        }

        /// <summary>
        /// Initializes a new instance of the ScopeAssignment class.
        /// </summary>
        /// <param name="id">Fully qualified resource Id for the resource. Ex -
        /// /subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/{resourceProviderNamespace}/{resourceType}/{resourceName}</param>
        /// <param name="name">The name of the resource</param>
        /// <param name="type">The type of the resource. Ex-
        /// Microsoft.Compute/virtualMachines or
        /// Microsoft.Storage/storageAccounts.</param>
        /// <param name="provisioningState">Provisioning state of the
        /// ManagedNetwork resource. Possible values are: 'Updating',
        /// 'Deleting', and 'Failed'. Possible values include: 'Updating',
        /// 'Deleting', 'Failed', 'Succeeded'</param>
        /// <param name="etag">A unique read-only string that changes whenever
        /// the resource is updated.</param>
        /// <param name="assignedManagedNetwork">The managed network ID with
        /// scope will be assigned to.</param>
        public ScopeAssignment(string id = default(string), string name = default(string), string type = default(string), string provisioningState = default(string), string etag = default(string), string assignedManagedNetwork = default(string))
            : base(id, name, type)
        {
            ProvisioningState = provisioningState;
            Etag = etag;
            AssignedManagedNetwork = assignedManagedNetwork;
            CustomInit();
        }

        /// <summary>
        /// An initialization method that performs custom operations like setting defaults
        /// </summary>
        partial void CustomInit();

        /// <summary>
        /// Gets provisioning state of the ManagedNetwork resource. Possible
        /// values are: 'Updating', 'Deleting', and 'Failed'. Possible values
        /// include: 'Updating', 'Deleting', 'Failed', 'Succeeded'
        /// </summary>
        [JsonProperty(PropertyName = "properties.provisioningState")]
        public string ProvisioningState { get; private set; }

        /// <summary>
        /// Gets a unique read-only string that changes whenever the resource
        /// is updated.
        /// </summary>
        [JsonProperty(PropertyName = "properties.etag")]
        public string Etag { get; private set; }

        /// <summary>
        /// Gets or sets the managed network ID with scope will be assigned to.
        /// </summary>
        [JsonProperty(PropertyName = "properties.assignedManagedNetwork")]
        public string AssignedManagedNetwork { get; set; }

    }
}
