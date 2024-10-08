﻿using System.Text;
using Dapper;
using Npgsql;
using Wireguard.Api.Data.Dtos;
using Wireguard.Api.Data.Entities;
using Wireguard.Api.Helpers;

namespace Wireguard.Api.Data.Repositories;

public class PeerRepository(
    IConfiguration configuration,
    IInterfaceRepository interfaceRepository,
    IIpAddressRepository ipAddressRepository) : IPeerRepository
{
    public async Task<bool> InsertAsync(AddPeerDto peer, string interfaceName,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            new NpgsqlConnection(configuration.GetValue<string>("DatabaseSettings:ConnectionString"));

        await connection.OpenAsync(cancellationToken);

        var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var @interface = await interfaceRepository
            .GetInterfaceByNameAsync(interfaceName);

        if (@interface == null) throw new ApplicationException($"not found interface by name {interfaceName}");

        List<IpAddress> ipAddresses = await ipAddressRepository.GetIpAddressByInterfaceIdAsync(@interface.Id);

        if (peer.Count > ipAddresses.Count(x => x.Available == true)) throw new ApplicationException("peer is full");

        if (peer.Bulk)
        {
            try
            {
                List<int> idsIp = new List<int>();

                while (0 < peer.Count)
                {
                    var availableIp = ipAddresses.FirstOrDefault(x => x.Available);

                    ipAddresses.RemoveAt(ipAddresses.IndexOf(availableIp));

                    idsIp.Add(availableIp.Id);

                    if (availableIp == null) throw new ApplicationException("no available ip address");

                    KeyPair keyPair = KeyGeneratorHelper.GenerateKeys();

                    peer.Name = Guid.NewGuid().ToString("N");
                    peer.PublicKey = keyPair.PublicKey;
                    peer.PresharedKey = keyPair.PresharedKey;
                    peer.AllowedIPs = new List<string> { availableIp.Ip };

                    string command = """
                                        INSERT INTO PEER (InterfaceId,
                                                          Name,
                                                          PublicKey,
                                                          PrivateKey,
                                                          PresharedKey,
                                                          AllowedIPs,
                                                          Mut,
                                                          EndpointAllowedIPs,
                                                          Dns,
                                                          PersistentKeepalive
                                                          ) Values (@InterfaceId,@Name,@PublicKey,@PrivateKey,@PresharedKey,@AllowedIPs,@Mut,@EndpointAllowedIPs,@Dns,@PersistentKeepalive)
                                     """;

                    int response = await connection.ExecuteAsync(command, new
                    {
                        interfaceId = @interface.Id,
                        peer.Name,
                        keyPair.PublicKey,
                        keyPair.PrivateKey,
                        keyPair.PresharedKey,
                        AllowedIPs = string.Join(",", peer.AllowedIPs),
                        Mut = peer.Mtu,
                        EndpointAllowedIPs = peer.EndpointAllowedIPs,
                        Dns = peer.Dns,
                        PersistentKeepalive = peer.PersistentKeepalive
                    });

                    if (response > 0 && !await WireguardHelpers.CreatePeer(peer, @interface))
                        throw new ApplicationException("failed to create peer");

                    peer.Count--;
                }

                await ipAddressRepository.OutOfReachIpAddressAsync(idsIp, connection, transaction);

                await transaction.CommitAsync(cancellationToken);
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }
        else
        {
            try
            {
                string query = """  
                                SELECT COUNT(*) FROM PEER
                                WHERE Name = @Name
                               """;

                int exist = await connection.QueryFirstOrDefaultAsync<int>(query, new { Name = peer.Name });

                if (exist > 0) throw new ApplicationException("peer by name is exist");

                string command = """
                                    INSERT INTO PEER (InterfaceId,
                                                      Name,
                                                      PublicKey,
                                                      PrivateKey,
                                                      PresharedKey,
                                                      AllowedIPs,
                                                      Mut,
                                                      EndpointAllowedIPs,
                                                      Dns,
                                                      PersistentKeepalive
                                                      ) Values (@InterfaceId,@Name,@PublicKey,@PrivateKey,@PresharedKey,@AllowedIPs,@Mut,@EndpointAllowedIPs,@Dns,@PersistentKeepalive)
                                 """;

                int response = await connection.ExecuteAsync(command, new
                {
                    peer.Name,
                    peer.PublicKey,
                    peer.EndPoint,
                    peer.PresharedKey,
                    AllowedIPs = string.Join(",", peer.AllowedIPs),
                    interfaceId = @interface.Id
                });

                if (response > 0 && !await WireguardHelpers.CreatePeer(peer, @interface))
                    throw new ApplicationException("failed to create peer");

                await transaction.CommitAsync(cancellationToken);

                return response > 0;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }
    }

    public async Task<FilterPeerDto> FilterPeerAsync(FilterPeerDto filter,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            new NpgsqlConnection(configuration.GetValue<string>("DatabaseSettings:ConnectionString"));

        await connection.OpenAsync(cancellationToken);

        var sqlBuilder = new StringBuilder();
        sqlBuilder.AppendLine("""
                                  SELECT P.*, I.*
                                  FROM PEER P
                                  JOIN Interface I ON P.InterfaceId = I.Id
                                  WHERE I.Name = @InterfaceName
                              """);

        // Add the condition for Name only if it's not null or empty
        if (!string.IsNullOrWhiteSpace(filter.Name))
        {
            sqlBuilder.AppendLine("AND P.Name ILIKE '%' || @Name || '%'");
        }

        sqlBuilder.AppendLine("ORDER BY P.Id LIMIT @Take OFFSET @Skip;");

        var sql = sqlBuilder.ToString();

        // Execute the query to retrieve data
        var peers = await connection.QueryAsync<Peer, Interface, Peer>(
            sql,
            (peer, @interface) =>
            {
                peer.InterfaceId = @interface.Id;
                return peer;
            },
            new
            {
                Name = string.IsNullOrWhiteSpace(filter.Name) ? null : filter.Name, // Pass null if Name is empty
                filter.InterfaceName,
                filter.Take,
                filter.Skip
            },
            splitOn: "Id"
        );

        // Dynamically building the SQL query for counting records
        var countSqlBuilder = new StringBuilder();
        countSqlBuilder.AppendLine("""
                                       SELECT COUNT(*)
                                       FROM PEER P
                                       JOIN Interface I ON P.InterfaceId = I.Id
                                       WHERE I.Name = @InterfaceName
                                   """);

        if (!string.IsNullOrWhiteSpace(filter.Name))
        {
            countSqlBuilder.AppendLine("AND P.Name ILIKE '%' || @Name || '%'");
        }

        var countSql = countSqlBuilder.ToString();

        // Execute the query to count records
        var countPeer = await connection.ExecuteScalarAsync<int>(countSql, new
        {
            Name = string.IsNullOrWhiteSpace(filter.Name) ? null : filter.Name, // Pass null if Name is empty
            filter.InterfaceName
        });

        return new FilterPeerDto
        {
            Take = filter.Take,
            Skip = filter.Skip,
            Name = filter.Name,
            InterfaceName = filter.InterfaceName,
            PublicKey = filter.PublicKey,
            CountPeer = countPeer,
            Peers = peers.ToList()
        };
    }

    public async Task<string> GeneratePeerContentConfig(string name)
    {
        await using var connection =
            new NpgsqlConnection(configuration.GetValue<string>("DatabaseSettings:ConnectionString"));

        await connection.OpenAsync();

        var query = """
                    SELECT * FROM PEER P
                    JOIN Interface I ON P.InterfaceId = I.Id
                    WHERE P.Name = @Name
                    """;

        var content = await connection.QueryAsync<Peer, Interface, string>(query, (peer, @interface) =>
        {
            if (@interface is null) throw new ApplicationException("interface not found");
            return peer switch
            {
                null => throw new ApplicationException("peer by name is null"),
                _ => $"""
                      [Interface]
                      PrivateKey = {peer.PrivateKey}
                      Address = {peer.AllowedIPs}
                      MTU = {peer.Mtu}
                      DNS = {peer.Dns}

                      [Peer]
                      PublicKey = {@interface.PublicKey}
                      AllowedIPs = {string.Join(", ", peer.AllowedIPs)}
                      Endpoint = {@interface.EndPoint}:{@interface.ListenPort}
                      PersistentKeepalive = {peer.PersistentKeepalive} 

                      """
            };
        }, new { Name = name });

        return content.FirstOrDefault();
    }
}