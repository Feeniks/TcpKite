namespace TcpKite

open System
open System.Net
open System.Net.Sockets
open System.Threading

open TcpKite.Protocol

module Proxy = 

    type IPortManager = 
        abstract member Get : unit -> int
        abstract member Return : int -> unit

    type private ProxyCmd = PC_NewClient of Socket | PC_Send of int * byte array | PC_SendNode of Node | PC_Recv | PC_KillClient of int | PC_Exit

    type private ProxyAgent = ProxyCmd MailboxProcessor

    type ProxyHandle = internal {
        socket:Socket;
        proxy:ProxyAgent;
    }

    let private idgen = ref 0

    let private readNodeHeader (s:Socket) = 
        let hbuf = Array.create 3 0uy
        s.Receive (hbuf, 3, SocketFlags.None) |> ignore
        (int hbuf.[0] <<< 16) + (int hbuf.[1] <<< 8) + (int hbuf.[2] <<< 0)

    let private readNodeData (s:Socket) (len:int) = 
        let recv (buf:byte array) (offset:int) (size:int) = s.Receive (buf, offset, size, SocketFlags.None)
        let rec readAll (buf:byte array) (read:int)  = 
            match read with
            | r when r = len -> ()
            | _ -> (recv buf read (len - read)) + read |> readAll buf
        let buf = Array.create len 0uy
        readAll buf 0
        buf

    let private readNode (s:Socket) = 
        let len = readNodeHeader s
        let data = readNodeData s len
        parseNode data

    let private read (s:Socket) = 
        match s.Available with
        | x when x >= 3 -> readNode s |> Some
        | _ -> None

    let rec private sendData (s:Socket) (buf:byte array) = 
        match Array.length buf with
        | 0 -> ()
        | _ ->
            let sent = s.Send buf
            Array.sub buf sent (buf.Length - sent) |> sendData s

    let private sendNode (s:Socket) (n:Node) = 
        try
            let buf = serializeNode n
            let len = buf.Length
            let header = [|byte ((len &&& 0xff0000) >>> 16); byte ((len &&& 0x00ff00) >>> 8); byte (len &&& 0x0000ff)|]
            Array.append header buf |> sendData s
            Choice1Of2 ()
        with
        | _ as ex -> Choice2Of2 ex

    let private makeCreateNode (cid:int) = makeNode cid "create"

    let private makeDataNode (cid:int) (data:byte array) = 
        let node = let n = makeNode cid "data" in { n with Node.data = data; }
        node

    let private makeKillNode (cid:int) = makeNode cid "kill"

    let private killClient (cid:int) (clients:Map<int,Client.ClientHandle>) = 
        match Map.tryFind cid clients with
        | None -> clients
        | Some handle ->
            Client.destroy handle
            Map.remove cid clients

    let private createClientHandle (id:int) (s:Socket) (timeout:TimeSpan) (a:ProxyAgent) = 
        let iproxy () = 
            {
                new Client.IProxy with
                    member x.Send data = PC_Send (id, data) |> a.Post
                    member x.Kill () = PC_KillClient id |> a.Post
            }
        let proxyInterface = iproxy ()

        let handle = Client.create s timeout proxyInterface

        handle.receiver.Error.Add (fun ex -> raise ex)
        handle.sender.Error.Add (fun ex -> raise ex)

        handle

    let rec private proxy (cf:unit -> Socket) (ef:unit -> unit) (sock:Socket) (timeout:TimeSpan) (clients:Map<int, Client.ClientHandle>) (a:ProxyAgent) = async {
        let! msg = a.Receive ()

        match msg with
        | PC_NewClient s ->
            let id = Interlocked.Increment idgen

            let handle = createClientHandle id s timeout a

            let clients' = Map.add id handle clients

            makeCreateNode id |> PC_SendNode |> a.Post

            return! proxy cf ef sock timeout clients' a

        | PC_Send (cid, data) ->
            makeDataNode cid data |> PC_SendNode |> a.Post

            return! proxy cf ef sock timeout clients a

        | PC_SendNode n ->
            match sendNode sock n with
            | Choice1Of2 () -> ()
            | Choice2Of2 _ -> a.Post PC_Exit

            return! proxy cf ef sock timeout clients a
        | PC_Recv -> 
            match read sock with
            | Some n ->
                let clients' = 
                    match n.tag with
                    | "create" -> 
                        let s = cf ()
                        let handle = createClientHandle n.cid s timeout a
                        Map.add n.cid handle clients
                    | "data" ->
                        match Map.tryFind n.cid clients with
                        | Some handle -> Client.write handle n.data
                        | _ -> ()
                        clients
                    | "kill" -> killClient n.cid clients
                    | "ping" -> clients
                    | _ -> sprintf "Unsupport node %A" n |> failwith

                a.Post PC_Recv

                return! proxy cf ef sock timeout clients' a
            | None ->
                async {
                    do! Async.Sleep 20
                    a.Post PC_Recv
                } |> Async.Start

                return! proxy cf ef sock timeout clients a

        | PC_KillClient cid ->
            let clients' = killClient cid clients

            makeKillNode cid |> PC_SendNode |> a.Post

            return! proxy cf ef sock timeout clients' a

        | PC_Exit ->
            Map.toSeq clients |> Seq.iter (fun (_,h) -> Client.destroy h)
            try sock.Close () with | _ -> ()
            ef ()
    }

    let private createConnection (ep:IPEndPoint) = 
        let srv = new Socket (AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        srv.Connect ep
        srv

    let rec private readConnectionParm (s:Socket) (ts:DateTime) = 
        match s.Available with
        | x when x < 3 -> 
            match (DateTime.Now - ts).TotalMilliseconds with | x when x > 5000.0 -> failwith "Could not negotiate connection" | _ -> ()
            readConnectionParm s ts
        | _ ->
            let hbuf = Array.create 3 0uy
            s.Receive (hbuf, 3, SocketFlags.None) |> ignore
            (int hbuf.[0] <<< 16) + (int hbuf.[1] <<< 8) + (int hbuf.[2] <<< 0)

    let private sendConnectionParm (s:Socket) (parm:int) = 
        let buf = [|byte ((parm &&& 0xff0000) >>> 16); byte ((parm &&& 0x00ff00) >>> 8); byte (parm &&& 0x0000ff)|]
        s.Send buf |> ignore

    let private newClient (handle:ProxyHandle) (s:Socket) = PC_NewClient s |> handle.proxy.Post

    let createClient (serverEP:IPEndPoint) (localEP:IPEndPoint) (timeout:TimeSpan) = 
        let cf = (fun () -> createConnection localEP)
        let ef () = failwith "Connection failure"

        let server = createConnection serverEP

        int timeout.TotalMilliseconds |> sendConnectionParm server
        let serverPort = readConnectionParm server DateTime.Now

        let agent = proxy cf ef server timeout Map.empty |> ProxyAgent.Start

        agent.Post PC_Recv

        agent.Error.Add (fun ex -> raise ex)

        serverPort, { ProxyHandle.socket = server; ProxyHandle.proxy = agent; }

    let rec private serverListen (s:Socket) (proxy:ProxyHandle) =
        let client = s.Accept ()

        newClient proxy client

        serverListen s proxy
        

    let private handleClient (client:Socket) (pm:IPortManager) = async {
        try
            let timeoutMS = readConnectionParm client DateTime.Now
            let port = pm.Get ()
            sendConnectionParm client port

            let server = new Socket (AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            let serverEP = new IPEndPoint (IPAddress.Any, port)

            server.Bind serverEP
            server.Listen (100)

            let timeout = float timeoutMS |> TimeSpan.FromMilliseconds

            let cf () = failwith "Server cannot create connections"
            let ef () = 
                try  
                    pm.Return port
                    server.Close ()
                with | _ as ex -> ()

            let handle = { ProxyHandle.socket = client; ProxyHandle.proxy = proxy cf ef client timeout Map.empty |> ProxyAgent.Start; }
            handle.proxy.Error.Add (fun ex -> raise ex)
            handle.proxy.Post PC_Recv
            
            serverListen server handle
        with
        | _ -> ()
    }

    let rec private proxyListen (s:Socket) (pm:IPortManager) = 
        let client = s.Accept ()

        handleClient client pm |> Async.Start

        proxyListen s pm

    let createServer (localEP:IPEndPoint) (portManager:IPortManager) = 
        let proxyServer = new Socket (AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)

        proxyServer.Bind localEP
        proxyServer.Listen (100)

        proxyListen proxyServer portManager