targetScope = 'resourceGroup'

// Network prerequisites for attaching a delegated subnet to the EXISTING
// `bnx-buildtest-env` Container Apps environment.
//
// Why this template exists separately from main.bicep:
// attaching a subnet to a live environment must be the ONLY change in its
// ARM request. Deploying main.bicep would re-evaluate the ACR, storage
// account, role assignments and the runner job in the same request, which
// violates that constraint. So the migration deploys the network resources
// here first, then PATCHes `vnetConfiguration` onto the environment on its
// own. See infra/buildtest/README-migration.md.
//
// A GREENFIELD deploy does NOT use this template -- main.bicep declares the
// same three resources inline and resolves the ordering itself.
//
// Resource names, address space and delegation MUST stay byte-identical to
// the corresponding declarations in main.bicep. They describe the same
// resources: if they drift, a subsequent main.bicep deployment silently
// mutates network topology that this template established.
//
// Constraints that bind this template (all verified against the control
// plane, see README-migration.md):
//   * the subnet MUST be delegated to Microsoft.App/environments;
//   * a NAT gateway MUST be attached to it, so egress leaves on an address
//     we own rather than a shared Container Apps platform address (ACA is a
//     Hosted-On-Behalf-Of service);
//   * the CIDR must avoid 169.254.0.0/16, 172.30.0.0/16, 172.31.0.0/16,
//     192.0.2.0/24 and the workload-profile reservations 100.100.0.0/17,
//     100.100.128.0/19, 100.100.160.0/19, 100.100.192.0/19. 10.0.0.0/16
//     clears all of them;
//   * removing a subnet from an environment later is NOT supported -- the
//     attach that follows this deployment is one-way.

param location string = resourceGroup().location

var tags = {
  workload: 'botnexus-buildtest'
  managedBy: 'bicep'
  authentication: 'managed-identity-only'
}

var vnetName = 'bnx-buildtest-vnet'
var subnetName = 'bnx-buildtest-aca-subnet'
var natGatewayName = 'bnx-buildtest-nat'
var publicIpName = 'bnx-buildtest-nat-pip'

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

// `subnetId` is what the follow-up PATCH puts in
// `properties.vnetConfiguration.infrastructureSubnetId`. The migration guide
// re-reads it with `az network vnet subnet show`; this output is the same
// value without a second control-plane round trip.
output subnetId string = vnet.properties.subnets[0].id
output vnetName string = vnet.name
output subnetName string = subnetName
output natGatewayName string = natGateway.name
output publicIpName string = publicIp.name
output natPublicIpAddress string = publicIp.properties.ipAddress
