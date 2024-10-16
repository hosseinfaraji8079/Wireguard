﻿using Wireguard.Api.Data.Entities;
using Wireguard.Api.Data.Enums;

namespace Wireguard.Api.Data.Dtos;

public class AddPeerDto
{
    public string? Name { get; set; }
    public string? PublicKey { get; set; }
    public string? PresharedKey { get; set; }
    public List<string>? AllowedIPs { get; set; }
    public string? EndPoint { get; set; }
    public bool Bulk { get; set; }
    public int Count { get; set; } = 1;
    public string? Dns { get; set; }
    public int? Mtu { get; set; } = 1420;
    public int? PersistentKeepalive { get; set; } = 21;
    public string EndpointAllowedIPs { get; set; } = "0.0.0.0/0";
    public long ExpireTime { get; set; } = 0;
    public long TotalVolume { get; set; } = 0;
    public string? Status { get; set; } = PeerStatus.onhold.ToString();
    public long OnHoldExpireDurection { get; set; }
    public AddPeerDto()
    {
    }
    public AddPeerDto(Peer peer)
    {
        AllowedIPs = peer.AllowedIPs.Split(",").ToList();
        Name = peer.Name;
        PublicKey = peer.PublicKey;
        PresharedKey = peer.PresharedKey;
        EndpointAllowedIPs = peer.EndpointAllowedIPs;
        ExpireTime = peer.ExpireTime;
        TotalVolume = peer.TotalVolume;
        Status = peer.Status;
        OnHoldExpireDurection = peer.OnHoldExpireDurection;
    }
}