namespace TcpKite

open System.Text

module Protocol = 

    type Node = {
        cid:int;
        tag:string;
        data:byte array;
    }

    let private encodeInt32 (v:int) = [byte ((v &&& 0xff0000) >>> 24); byte ((v &&& 0xff0000) >>> 16); byte ((v &&& 0x00ff00) >>> 8); byte (v &&& 0x0000ff)]

    let private encodeInt24 (v:int) = [byte ((v &&& 0xff0000) >>> 16); byte ((v &&& 0x00ff00) >>> 8); byte (v &&& 0x0000ff)]

    let private encodeInt16 (v:int) = [byte ((v &&& 0xff00) >>> 8); byte (v &&& 0x00ff)]

    let private encodeInt8 (v:int) = [byte (v &&& 0xff)]

    let private encodeString (s:string) = Encoding.UTF8.GetBytes s |> List.ofArray

    let private peekInt8o (o:int) (buf:byte array) = int buf.[o]
    let private peekInt8 = peekInt8o 0

    let private peekInt16o (o:int) (buf:byte array) = (int buf.[o] <<< 8) ||| (int buf.[o + 1] <<< 0)
    let private peekInt16 = peekInt16o 0

    let private peekInt24o (o:int) (buf:byte array) = (int buf.[o] <<< 16) + (int buf.[o + 1] <<< 8) + (int buf.[o + 2] <<< 0)
    let private peekInt24 = peekInt24o 0

    let private peekInt32o (o:int) (buf:byte array) = (int buf.[o] <<< 24) + (int buf.[o + 1] <<< 16) + (int buf.[o + 2] <<< 8) + (int buf.[o + 3] <<< 0)
    let private peekInt32 = peekInt32o 0

    let private takeInt8 (buf:byte array) = (peekInt8 buf, buf.[1..])

    let private takeInt16 (buf:byte array) = (peekInt16 buf, buf.[2..])

    let private takeInt24 (buf:byte array) = (peekInt24 buf, buf.[3..])

    let private takeInt32 (buf:byte array) = (peekInt32 buf, buf.[4..])

    let private takeString (len:int) (buf:byte array) = 
        let str = buf.[..(len - 1)] |> Encoding.UTF8.GetString
        (str,buf.[len..])

    let makeNode (cid:int) (tag:string) = { Node.cid = cid; Node.tag = tag; Node.data = [||]; }

    let parseNode (buf:byte array) = 
        let (cid,buf) = takeInt24 buf
        let (taglen,buf) = takeInt8 buf
        let (tag,buf) = takeString taglen buf

        {  
            Node.cid = cid;
            Node.tag = tag;
            Node.data = buf;
        }

    let serializeNode (n:Node) = 
        let tagdata = encodeString n.tag
        (encodeInt24 n.cid) @ (encodeInt8 tagdata.Length) @ tagdata @ (List.ofArray n.data) |> Array.ofList