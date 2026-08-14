targetScope = 'resourceGroup'

param location string = resourceGroup().location
param operatorObjectId string
param suffix string
// No default: the tag is content-derived by Deploy-BuildTestInfrastructure.ps1 and always passed
// explicitly (#2900). A default here was a THIRD source of truth for the runner version -- it
// still read '0.1.4' while the script said '0.1.11' and the deployed job ran '0.1.15'. A required
// parameter cannot silently deploy a stale image.
param runnerImageTag string

var tags = {
  workload: 'botnexus-buildtest'
  managedBy: 'bicep'
  authentication: 'managed-identity-only'
}
var acrName = 'bnxbt${suffix}acr'
var storageName = 'bnxbt${suffix}sa'
var identityName = 'bnx-buildtest-runner'
var environmentName = 'bnx-buildtest-env'
var jobName = 'bnx-buildtest-runner'
var vnetName = 'bnx-buildtest-vnet'
var subnetName = 'bnx-buildtest-aca-subnet'
var natGatewayName = 'bnx-buildtest-nat'
var publicIpName = 'bnx-buildtest-nat-pip'
var acrPullRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
var blobContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: tags
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: acrName
  location: location
  tags: tags
  sku: {
    name: 'Premium'
  }
  properties: {
    adminUserEnabled: false
    anonymousPullEnabled: false
    publicNetworkAccess: 'Enabled'
    policies: {
      retentionPolicy: {
        days: 7
        status: 'enabled'
      }
      azureADAuthenticationAsArmPolicy: {
        status: 'enabled'
      }
    }
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Enabled'
    accessTier: 'Hot'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: 7
    }
    containerDeleteRetentionPolicy: {
      enabled: true
      days: 7
    }
  }
}

resource sources 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'sources'
  properties: {
    publicAccess: 'None'
  }
}

resource artifacts 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'artifacts'
  properties: {
    publicAccess: 'None'
  }
}

// ---------------------------------------------------------------- networking
// The ACA environment MUST sit in a delegated subnet behind a NAT gateway carrying OUR public IP.
//
// Azure Container Apps is a HOBO (Hosted-On-Behalf-Of) service: with no subnet, outbound traffic
// leaves on IP addresses owned by the Container Apps platform rather than by us. Those platform
// addresses are shared and are being reclassified, so an environment that relies on them will
// eventually lose access to resources that authorise on source IP. Owning the egress address is
// the durable fix.
//
// This attaches IN PLACE -- no rebuild. See infra/buildtest/README-migration.md. The constraints
// that bind this template: the subnet MUST be delegated to Microsoft.App/environments,
// a NAT gateway MUST be attached to it, `internal` MUST be false (the environment needs a public
// IP), and the CIDR must avoid the AKS-reserved ranges plus the workload-profile reservations at
// 100.100.0.0/17 and 100.100.128.0/19, .160.0/19, .192.0/19. 10.0.0.0/16 clears all of them.
// Attaching the subnet also CHANGES the environment's frontend IP, and removing a subnet later is
// not supported -- this is one-way.
resource publicIp 'Microsoft.Network/publicIPAddresses@2023-11-01' = {
  name: publicIpName
  location: location
  tags: tags
  sku: {
    name: 'Standard'
  }
  properties: {
    publicIPAllocationMethod: 'Static'
  }
}

resource natGateway 'Microsoft.Network/natGateways@2023-11-01' = {
  name: natGatewayName
  location: location
  tags: tags
  sku: {
    name: 'Standard'
  }
  properties: {
    publicIpAddresses: [
      {
        id: publicIp.id
      }
    ]
    idleTimeoutInMinutes: 4
  }
}

resource vnet 'Microsoft.Network/virtualNetworks@2023-11-01' = {
  name: vnetName
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [
        '10.0.0.0/16'
      ]
    }
    subnets: [
      {
        name: subnetName
        properties: {
          // Workload-profile environments need at least a /27; /23 leaves headroom for scale-out.
          addressPrefix: '10.0.0.0/23'
          natGateway: {
            id: natGateway.id
          }
          serviceEndpoints: []
          delegations: [
            {
              name: 'Microsoft.App.environments'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
              type: 'Microsoft.Network/virtualNetworks/subnets/delegations'
            }
          ]
        }
        type: 'Microsoft.Network/virtualNetworks/subnets'
      }
    ]
    virtualNetworkPeerings: []
    enableDdosProtection: false
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: environmentName
  location: location
  tags: tags
  properties: {
    vnetConfiguration: {
      internal: false
      infrastructureSubnetId: vnet.properties.subnets[0].id
    }
    // This environment is a WORKLOAD-PROFILES environment whose only profile happens to be named
    // 'Consumption' -- NOT a legacy Consumption-only environment. The distinction decides whether
    // a subnet can be attached in place, and reading the profile NAME instead of the array's
    // presence produced a wrong "must recreate" call first time round. A legacy Consumption-only
    // environment has `workloadProfiles` absent; ours has it populated, so the in-place path in
    // the in-place attach path applies. Verified against the control plane: PATCHing a
    // vnetConfiguration onto the live environment fails on the SUBNET being missing, not on the
    // environment type.
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    zoneRedundant: false
  }
}

resource job 'Microsoft.App/jobs@2024-03-01' = {
  name: jobName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      // 20 minutes. A full core run measures ~13-15 min, so this is a realistic budget
      // rather than an arbitrary ceiling: a lane that breaches it has genuinely hung (the
      // 2026-08-06 parallel test wedged 3 of 5 lanes past 34 min) instead of quietly
      // burning two hours before anyone notices. Raise it deliberately if honest run time
      // grows - Invoke-AzureBuildTest reports the margin so the breach is visible.
      replicaTimeout: 1200
      replicaRetryLimit: 0
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      registries: [
        {
          server: registry.properties.loginServer
          identity: identity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'runner'
          image: '${registry.properties.loginServer}/botnexus-buildtest-runner:${runnerImageTag}'
          resources: {
            cpu: json('4.0')
            memory: '8Gi'
          }
          env: [
            {
              name: 'AZURE_CLIENT_ID'
              value: identity.properties.clientId
            }
            {
              name: 'SOURCE_BLOB_URL'
              value: 'https://${storage.name}.blob.${az.environment().suffixes.storage}/sources/source.tar.gz'
            }
            {
              name: 'ARTIFACT_BLOB_URL'
              value: 'https://${storage.name}.blob.${az.environment().suffixes.storage}/artifacts'
            }
            {
              name: 'TEST_MODE'
              value: 'impacted'
            }
            {
              name: 'BASE_REF'
              value: 'origin/main'
            }
          ]
        }
      ]
    }
  }
}

resource runnerAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, identity.id, acrPullRoleId)
  scope: registry
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: acrPullRoleId
  }
}

resource runnerBlobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, identity.id, blobContributorRoleId)
  scope: storage
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: blobContributorRoleId
  }
}

resource operatorBlobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, operatorObjectId, blobContributorRoleId)
  scope: storage
  properties: {
    principalId: operatorObjectId
    principalType: 'User'
    roleDefinitionId: blobContributorRoleId
  }
}

output acrName string = registry.name
output storageAccountName string = storage.name
output jobName string = job.name
output runnerIdentityId string = identity.id
