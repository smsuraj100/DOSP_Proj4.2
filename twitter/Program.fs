open Suave
open System.Collections.Generic
open Suave.Filters
open Suave.Operators
open Suave.Successful
open Suave.Writers
open Newtonsoft.Json
open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket

type RegistrationResponse = {
    RegistrationSuccess: bool;
}

type SocketRequest = {
    Username: string;
    Password: string;
    Tweet: string;
    Login: bool;
}

type SocketResponse = {
    SocketSuccess: bool;
}

type FollowResponse = {
    FollowSuccess: bool
}

type FollowersResponse = {
    Followers: List<string>
}

type FollowingsResponse = {
    Followings: List<string>
}

type TweetRequest = {
    Tweet: string;
    User: string;
}

type MentionsResponse = {
    Mentions: List<TweetRequest>;
}

type HashtagsResponse = {
    Hashtags: List<TweetRequest>;
}

type TimelineResponse = {
    Timeline: List<TweetRequest>;
}
  
type TweetsResponse = {
    Tweets: List<TweetRequest>;
}

type TweetResponse = {
    TweetSuccess: bool
}

type AllUsers = {
    AllUsers: List<string>
}

let registeredUsers = Dictionary<string, int * string>() //user id vs live or not
let followers = Dictionary<string, List<string>>() //userId vs their followers
let followings = Dictionary<string, List<string>>() //userId vs users they are following
let tweets = Dictionary<string, List<TweetRequest>>()
let timelines = Dictionary<string, List<TweetRequest>>()
let mentions = Dictionary<string, List<TweetRequest>>()
let hashtags = Dictionary<string, List<TweetRequest>>()
let sockets = Dictionary<string, WebSocket>()

//low level functions
let getString (rawForm: byte[]) =
    System.Text.Encoding.UTF8.GetString(rawForm)

let fromJson<'a> json =
    JsonConvert.DeserializeObject(json, typeof<'a>) :?> 'a

let updateMetadata tweetObj =
    let tweet = tweetObj.Tweet
    let parts = tweet.Split " "
    let mentioned = new List<string>()
    for part in parts do
        if part.[0] = '#' then
            if not(hashtags.ContainsKey(part.[1..])) then
                let mutable hashtagsList = new List<TweetRequest>()
                hashtags.Add(part.[1..], hashtagsList)
            hashtags.Item(part.[1..]).Add(tweetObj)
        else if part.[0] = '@' then
            if not(mentions.ContainsKey(part.[1..])) then
                let mutable mentionsList = new List<TweetRequest>()
                mentions.Add(part.[1..], mentionsList)
                mentioned.Add(part.[1..])
            mentions.Item(part.[1..]).Add(tweetObj)
    mentioned

let respond response =
    response
    |> JsonConvert.SerializeObject
    |> OK
    >=> setMimeType "application/json"
    
let getFollowers id =
    printfn "Handling getFollowers request by %s" id
    { Followers = followers.Item(id) }
    |> respond

let getFollowings id =
    printfn "Handling getFollowings request by %s" id
    { Followings = followings.Item(id) }
    |> respond

let getMentions mention =
    printfn "Fetching mentions named: %s" mention
    { Mentions = mentions.Item(mention)}
    |> respond

let getTimeline id =
    printfn "Handling getTimeline request by %s" id
    { Timeline = timelines.Item(id) }
    |> respond

let getTweets id =
    printfn "Handling getTweets request by %s" id
    { Tweets = tweets.Item(id)}
    |> respond

let getHashtags hashtag =
    printfn "Fetching hashtags named: %s" hashtag
    { Hashtags = hashtags.Item(hashtag)}
    |> respond

let getUsers id =
    printfn "Handling fetch all users request by: %s" id
    { AllUsers = new List<string>(registeredUsers.Keys) }
    |> respond

let registerUser id password =
    printfn "Handling registerUser request by %s" id
    let mutable tuple = (0, password)
    registeredUsers.Add(id, tuple)
    let mutable followersList = new List<string>()
    let mutable followingsList = new List<string>()
    let mutable tweetsList = new List<TweetRequest>()
    let mutable timelinesList = new List<TweetRequest>()
    followers.Add(id, followersList)
    followings.Add(id, followingsList)
    tweets.Add(id, tweetsList)
    timelines.Add(id, timelinesList)
    { RegistrationSuccess = true }
    |> JsonConvert.SerializeObject
    |> OK
    >=> setMimeType "application/json"

let followUser myId theirId =
    printfn "Handling followUser request by %s" myId
    followers.Item(theirId).Add(myId)
    followings.Item(myId).Add(theirId)
    { FollowSuccess = true }
    |> JsonConvert.SerializeObject
    |> OK
    >=> setMimeType "application/json"

let ws (webSocket : WebSocket) (context : HttpContext) =
    socket {
        let mutable loop = true
        while loop do
            let! msg = webSocket.read()
            match msg with
            | (Text, data, true) ->
                let req = data |> getString |> fromJson<SocketRequest>
                let mutable flag = true
                if req.Login then
                    if req.Password.Equals(snd(registeredUsers.Item(req.Username))) then
                        if sockets.ContainsKey(req.Username) then
                            sockets.Item(req.Username) <- webSocket
                        else
                            sockets.Add(req.Username, webSocket)
                        registeredUsers.Item(req.Username) <- (1, snd(registeredUsers.Item(req.Username))) //logged in
                        printfn "Connected with %s" req.Username
                    else
                        printfn "Wrong credentials"
                        flag <- false
                else
                    let tweetM = { Tweet = req.Tweet; User = req.Username }
                    let mentioned = updateMetadata tweetM
                    let followers = followers.Item(req.Username)
                    let mutable toSendList = new List<string>()
                    for m in mentioned do
                        toSendList.Add(m)
                    for f in followers do
                        if not(toSendList.Contains(f)) then
                            toSendList.Add(f)
                    tweets.Item(req.Username).Add(tweetM)
                    for follower in toSendList do
                        if fst(registeredUsers.Item(follower)) = 1 then
                            timelines.Item(follower).Add(tweetM)
                            let anotherSocket = sockets.Item(follower)
                            let anotherResponse = JsonConvert.SerializeObject tweetM
                            let anotherByteResponse = anotherResponse |> System.Text.Encoding.ASCII.GetBytes |> ByteSegment
                            do! anotherSocket.send Text anotherByteResponse true
                let response = JsonConvert.SerializeObject {SocketSuccess = flag}
                let byteResponse = response |> System.Text.Encoding.ASCII.GetBytes |> ByteSegment
                do! webSocket.send Text byteResponse true
            | (Close, _, _) ->
                for key in sockets.Keys do
                    if sockets.Item(key) = webSocket then
                        printfn "Disconnecting with %s" key
                        registeredUsers.Item(key) <- (0, snd(registeredUsers.Item(key))) //logged out
                let emptyResponse = [||] |> ByteSegment
                do! webSocket.send Close emptyResponse true
            | _ -> ()
    }
      
let app =
    choose
        [ GET >=> choose
            [ 
              pathScan "/register/%s/%s" (fun (userid, password) -> registerUser userid password )
              pathScan "/followers/%s" (fun id -> getFollowers id)
              pathScan "/followings/%s" (fun id -> getFollowings id)
              pathScan "/follow/%s/%s" (fun (myId, theirId) -> followUser myId theirId)
              pathScan "/mentions/%s" (fun mention -> getMentions mention)
              pathScan "/hashtags/%s" (fun hashtag -> getHashtags hashtag)
              pathScan "/timeline/%s" (fun id -> getTimeline id)
              pathScan "/tweets/%s" (fun id-> getTweets id)
              pathScan "/users/%s" (fun id-> getUsers id)
            ]

          path "/websocket" >=> handShake ws
        ]

[<EntryPoint>]
let main argv =
    startWebServer defaultConfig app
    0