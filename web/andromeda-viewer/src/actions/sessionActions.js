// Simple MD5 implementation for browser compatibility
function md5(string) {
  function md5_RotateLeft(lValue, iShiftBits) {
    return (lValue << iShiftBits) | (lValue >>> (32 - iShiftBits));
  }
  function md5_AddUnsigned(lX, lY) {
    var lX4, lY4, lX8, lY8, lResult;
    lX8 = (lX & 0x80000000);
    lY8 = (lY & 0x80000000);
    lX4 = (lX & 0x40000000);
    lY4 = (lY & 0x40000000);
    lResult = (lX & 0x3FFFFFFF) + (lY & 0x3FFFFFFF);
    if (lX4 & lY4) {
      return (lResult ^ 0x80000000 ^ lX8 ^ lY8);
    }
    if (lX4 | lY4) {
      if (lResult & 0x40000000) {
        return (lResult ^ 0xC0000000 ^ lX8 ^ lY8);
      } else {
        return (lResult ^ 0x40000000 ^ lX8 ^ lY8);
      }
    } else {
      return (lResult ^ lX8 ^ lY8);
    }
  }
  function md5_F(x, y, z) { return (x & y) | ((~x) & z); }
  function md5_G(x, y, z) { return (x & z) | (y & (~z)); }
  function md5_H(x, y, z) { return (x ^ y ^ z); }
  function md5_I(x, y, z) { return (y ^ (x | (~z))); }
  function md5_FF(a, b, c, d, x, s, ac) {
    a = md5_AddUnsigned(a, md5_AddUnsigned(md5_F(b, c, d), x));
    a = md5_AddUnsigned(a, ac);
    return md5_AddUnsigned(md5_RotateLeft(a, s), b);
  }
  function md5_GG(a, b, c, d, x, s, ac) {
    a = md5_AddUnsigned(a, md5_AddUnsigned(md5_G(b, c, d), x));
    a = md5_AddUnsigned(a, ac);
    return md5_AddUnsigned(md5_RotateLeft(a, s), b);
  }
  function md5_HH(a, b, c, d, x, s, ac) {
    a = md5_AddUnsigned(a, md5_AddUnsigned(md5_H(b, c, d), x));
    a = md5_AddUnsigned(a, ac);
    return md5_AddUnsigned(md5_RotateLeft(a, s), b);
  }
  function md5_II(a, b, c, d, x, s, ac) {
    a = md5_AddUnsigned(a, md5_AddUnsigned(md5_I(b, c, d), x));
    a = md5_AddUnsigned(a, ac);
    return md5_AddUnsigned(md5_RotateLeft(a, s), b);
  }
  function md5_ConvertToWordArray(string) {
    var lWordCount;
    var lMessageLength = string.length;
    var lNumberOfWords_temp1 = lMessageLength + 8;
    var lNumberOfWords_temp2 = (lNumberOfWords_temp1 - (lNumberOfWords_temp1 % 64)) / 64;
    var lNumberOfWords = (lNumberOfWords_temp2 + 1) * 16;
    var lWordArray = Array(lNumberOfWords - 1);
    var lBytePosition = 0;
    var lByteCount = 0;
    while (lByteCount < lMessageLength) {
      lWordCount = (lByteCount - (lByteCount % 4)) / 4;
      lBytePosition = (lByteCount % 4) * 8;
      lWordArray[lWordCount] = (lWordArray[lWordCount] | (string.charCodeAt(lByteCount) << lBytePosition));
      lByteCount++;
    }
    lWordCount = (lByteCount - (lByteCount % 4)) / 4;
    lBytePosition = (lByteCount % 4) * 8;
    lWordArray[lWordCount] = lWordArray[lWordCount] | (0x80 << lBytePosition);
    lWordArray[lNumberOfWords - 2] = (lMessageLength << 3);
    lWordArray[lNumberOfWords - 1] = (lMessageLength >>> 29);
    return lWordArray;
  }
  function md5_WordToHex(lValue) {
    var WordToHexValue = "", WordToHexValue_temp = "", lByte, lCount;
    for (lCount = 0; lCount <= 3; lCount++) {
      lByte = (lValue >>> (lCount * 8)) & 255;
      WordToHexValue_temp = "0" + lByte.toString(16);
      WordToHexValue = WordToHexValue + WordToHexValue_temp.substr(WordToHexValue_temp.length - 2, 2);
    }
    return WordToHexValue;
  }
  var x = Array();
  var k, AA, BB, CC, DD, a, b, c, d;
  var S11 = 7, S12 = 12, S13 = 17, S14 = 22;
  var S21 = 5, S22 = 9, S23 = 14, S24 = 20;
  var S31 = 4, S32 = 11, S33 = 16, S34 = 23;
  var S41 = 6, S42 = 10, S43 = 15, S44 = 21;
  x = md5_ConvertToWordArray(string);
  a = 0x67452301; b = 0xEFCDAB89; c = 0x98BADCFE; d = 0x10325476;
  for (k = 0; k < x.length; k += 16) {
    AA = a; BB = b; CC = c; DD = d;
    a = md5_FF(a, b, c, d, x[k + 0], S11, 0xD76AA478);
    d = md5_FF(d, a, b, c, x[k + 1], S12, 0xE8C7B756);
    c = md5_FF(c, d, a, b, x[k + 2], S13, 0x242070DB);
    b = md5_FF(b, c, d, a, x[k + 3], S14, 0xC1BDCEEE);
    a = md5_FF(a, b, c, d, x[k + 4], S11, 0xF57C0FAF);
    d = md5_FF(d, a, b, c, x[k + 5], S12, 0x4787C62A);
    c = md5_FF(c, d, a, b, x[k + 6], S13, 0xA8304613);
    b = md5_FF(b, c, d, a, x[k + 7], S14, 0xFD469501);
    a = md5_FF(a, b, c, d, x[k + 8], S11, 0x698098D8);
    d = md5_FF(d, a, b, c, x[k + 9], S12, 0x8B44F7AF);
    c = md5_FF(c, d, a, b, x[k + 10], S13, 0xFFFF5BB1);
    b = md5_FF(b, c, d, a, x[k + 11], S14, 0x895CD7BE);
    a = md5_FF(a, b, c, d, x[k + 12], S11, 0x6B901122);
    d = md5_FF(d, a, b, c, x[k + 13], S12, 0xFD987193);
    c = md5_FF(c, d, a, b, x[k + 14], S13, 0xA679438E);
    b = md5_FF(b, c, d, a, x[k + 15], S14, 0x49B40821);
    a = md5_GG(a, b, c, d, x[k + 1], S21, 0xF61E2562);
    d = md5_GG(d, a, b, c, x[k + 6], S22, 0xC040B340);
    c = md5_GG(c, d, a, b, x[k + 11], S23, 0x265E5A51);
    b = md5_GG(b, c, d, a, x[k + 0], S24, 0xE9B6C7AA);
    a = md5_GG(a, b, c, d, x[k + 5], S21, 0xD62F105D);
    d = md5_GG(d, a, b, c, x[k + 10], S22, 0x2441453);
    c = md5_GG(c, d, a, b, x[k + 15], S23, 0xD8A1E681);
    b = md5_GG(b, c, d, a, x[k + 4], S24, 0xE7D3FBC8);
    a = md5_GG(a, b, c, d, x[k + 9], S21, 0x21E1CDE6);
    d = md5_GG(d, a, b, c, x[k + 14], S22, 0xC33707D6);
    c = md5_GG(c, d, a, b, x[k + 3], S23, 0xF4D50D87);
    b = md5_GG(b, c, d, a, x[k + 8], S24, 0x455A14ED);
    a = md5_GG(a, b, c, d, x[k + 13], S21, 0xA9E3E905);
    d = md5_GG(d, a, b, c, x[k + 2], S22, 0xFCEFA3F8);
    c = md5_GG(c, d, a, b, x[k + 7], S23, 0x676F02D9);
    b = md5_GG(b, c, d, a, x[k + 12], S24, 0x8D2A4C8A);
    a = md5_HH(a, b, c, d, x[k + 5], S31, 0xFFFA3942);
    d = md5_HH(d, a, b, c, x[k + 8], S32, 0x8771F681);
    c = md5_HH(c, d, a, b, x[k + 11], S33, 0x6D9D6122);
    b = md5_HH(b, c, d, a, x[k + 14], S34, 0xFDE5380C);
    a = md5_HH(a, b, c, d, x[k + 1], S31, 0xA4BEEA44);
    d = md5_HH(d, a, b, c, x[k + 4], S32, 0x4BDECFA9);
    c = md5_HH(c, d, a, b, x[k + 7], S33, 0xF6BB4B60);
    b = md5_HH(b, c, d, a, x[k + 10], S34, 0xBEBFBC70);
    a = md5_HH(a, b, c, d, x[k + 13], S31, 0x289B7EC6);
    d = md5_HH(d, a, b, c, x[k + 0], S32, 0xEAA127FA);
    c = md5_HH(c, d, a, b, x[k + 3], S33, 0xD4EF3085);
    b = md5_HH(b, c, d, a, x[k + 6], S34, 0x4881D05);
    a = md5_HH(a, b, c, d, x[k + 9], S31, 0xD9D4D039);
    d = md5_HH(d, a, b, c, x[k + 12], S32, 0xE6DB99E5);
    c = md5_HH(c, d, a, b, x[k + 15], S33, 0x1FA27CF8);
    b = md5_HH(b, c, d, a, x[k + 2], S34, 0xC4AC5665);
    a = md5_II(a, b, c, d, x[k + 0], S41, 0xF4292244);
    d = md5_II(d, a, b, c, x[k + 7], S42, 0x432AFF97);
    c = md5_II(c, d, a, b, x[k + 14], S43, 0xAB9423A7);
    b = md5_II(b, c, d, a, x[k + 5], S44, 0xFC93A039);
    a = md5_II(a, b, c, d, x[k + 12], S41, 0x655B59C3);
    d = md5_II(d, a, b, c, x[k + 3], S42, 0x8F0CCC92);
    c = md5_II(c, d, a, b, x[k + 10], S43, 0xFFEFF47D);
    b = md5_II(b, c, d, a, x[k + 1], S44, 0x85845DD1);
    a = md5_II(a, b, c, d, x[k + 8], S41, 0x6FA87E4F);
    d = md5_II(d, a, b, c, x[k + 15], S42, 0xFE2CE6E0);
    c = md5_II(c, d, a, b, x[k + 6], S43, 0xA3014314);
    b = md5_II(b, c, d, a, x[k + 13], S44, 0x4E0811A1);
    a = md5_II(a, b, c, d, x[k + 4], S41, 0xF7537E82);
    d = md5_II(d, a, b, c, x[k + 11], S42, 0xBD3AF235);
    c = md5_II(c, d, a, b, x[k + 2], S43, 0x2AD7D2BB);
    b = md5_II(b, c, d, a, x[k + 9], S44, 0xEB86D391);
    a = md5_AddUnsigned(a, AA);
    b = md5_AddUnsigned(b, BB);
    c = md5_AddUnsigned(c, CC);
    d = md5_AddUnsigned(d, DD);
  }
  return (md5_WordToHex(a) + md5_WordToHex(b) + md5_WordToHex(c) + md5_WordToHex(d)).toLowerCase();
}

import ms from 'milliseconds'
import { v4 as uuid } from 'uuid'

import { viewerName, viewerVersion, viewerPlatform, viewerPlatformVersion } from '../viewerInfo'
import { getValueOf, getStringValueOf } from '../network/msgGetters'

import { saveAvatar, saveGrid } from './viewerAccount'
import {
  getLocalChatHistory,
  loadIMChats,
  deleteOldLocalChat,
  retrieveInstantMessages
} from './chatMessageActions'
import { getAllFriendsDisplayNames } from './friendsActions'
import { fetchSeedCapabilities } from './capabilities'
import LLSD from '../llsd'
import connectCircuit from './connectCircuit'

import { selectSavedAvatars, selectSavedGrids } from '../bundles/account'
import {
  startLogin,
  login as loginAction,
  loginFailed,
  startLogout,
  logout as didLogout,
  userWasKicked,

  selectAgentId,
  selectSessionId,
  selectIsLoggedIn
} from '../bundles/session'

// Actions for the session of an avatar

/**
 * Logon the user. It will post using fetch to the server.
 * @param {import('../bundles/names').MinimalAvatarName} avatarName Avatar Name
 * @param {string} password Password of the avatar.
 * @param {import('../types/viewer').Grid} grid Grid info
 * @param {boolean} save Should the avatar be saved.
 * @param {boolean} isNew Is this avatar new. False if this avatar was saved.
 * @returns {import('../store/configureStore').AppThunk}
 */
export function login (avatarName, password, grid, save, isNew) {
  return async (dispatch, getState, extra) => {
    if (selectIsLoggedIn(getState())) throw new Error('There is already an avatar logged in!')

    dispatch(startLogin({
      name: avatarName,
      grid,
      sync: save
    }))

    const hash = md5(string)
    hash.update(password, 'ascii')
    const finalPassword = '$1$' + hash.digest('hex')

    const viewerData = {
      loginUrl: grid.loginURL,
      userId: null
    }

    if (save) {
      const userData = await extra.db.get('_local/account')
      viewerData.userId = userData.accountId
    }

    const circuit = import('../network/circuit')

    const body = grid.isLLSDLogin
      ? await loginWithLLSD(viewerData, avatarName.firstName, avatarName.lastName, finalPassword)
      : await loginWithXmlRpc(viewerData, avatarName.firstName, avatarName.lastName, finalPassword)

    if (body.login !== 'true') {
      dispatch(loginFailed({ error: body.message }))
      throw new Error(body.message)
    }

    // save grid if it is new (do not save if login did fail)
    const gridExists = selectSavedGrids(getState()).some(savedGrid => savedGrid.name === grid.name)
    if (save && isNew && !gridExists) {
      await dispatch(saveGrid(grid))
    }

    const avatarIdentifier = `${body.agent_id}@${grid.name}`

    const avatarData = save && isNew
      ? await dispatch(saveAvatar(avatarName, body.agent_id, grid.name)) // adding new avatars
      : selectSavedAvatars(getState()).reduce((last, avatar) => { // for saved avatars
        if (last != null) return last

        return avatar.avatarIdentifier === avatarIdentifier
          ? avatar
          : last
      }, null)

    const localChatHistory = !isNew && save
      ? await dispatch(getLocalChatHistory(avatarData.dataSaveId))
      : []

    dispatch(loginAction({
      name: avatarName,
      save,
      avatarIdentifier: avatarData != null ? avatarData.avatarIdentifier : avatarIdentifier,
      dataSaveId: avatarData != null ? avatarData.dataSaveId : uuid(),
      grid,
      uuid: body.agent_id,
      sessionInfo: body,
      localChatHistory
    }))

    dispatch(loadIMChats())

    // Set the active circuit and connect to sim
    dispatch(connectToSim(body, await circuit))

    dispatch(fetchSeedCapabilities(body.seed_capability))
      .then(() => dispatch(getAllFriendsDisplayNames()))

    return body
  }
}

/**
 * Generates the headers send to the Andromeda-Server to proxy it.
 * @param {object} viewerData Object containing viewer related data.
 * @param {string} viewerData.loginUrl Login URL for the grid.
 * @param {string?} viewerData.userId id of a signed in user.
 * @param {boolean?} isLLSD Should it add a LLSD-header?
 */
function createProxyLoginHeaders ({ loginUrl, userId = null }, isLLSD = false) {
  const headers = new window.Headers()
  headers.append('Content-Type', 'application/json')
  headers.append('x-andromeda-login-url', loginUrl)
  headers.append('x-andromeda-login-content-type', isLLSD ? 'llsd' : 'xml-rpc')

  if (userId != null) {
    headers.append('x-andromeda-login-user-id', userId)
  }

  return headers
}

/**
 * Login using XML-RPC.
 * @param {object} viewerData Object containing viewer related data. (grid ...)
 * @param {string} first First name of the avatar.
 * @param {string} last Last name of the avatar.
 * @param {string} password Hashed and salted password.
 * @returns {object} Login response from the grid.
 */
async function loginWithXmlRpc (viewerData, first, last, password) {
  const loginData = {
    first,
    last,
    passwd: password,
    start: 'last',
    channel: viewerName,
    version: viewerVersion,
    platform: viewerPlatform,
    platform_version: viewerPlatformVersion,
    last_exec_event: 0,
    // mac and id0 will be added on the server side
    options: [
      'buddy-list',
      'inventory-root',
      'inventory-skeleton'
    ],
    agree_to_tos: 'true',
    read_critical: 'true'
  }

  const response = await window.fetch('/api/login', {
    method: 'POST',
    body: JSON.stringify(loginData),
    headers: createProxyLoginHeaders(viewerData)
  })

  const data = await response.json()
  data.andromedaSessionId = response.headers.get('x-andromeda-session-id')
  return data
}

/**
 * Login using LLSD (http://wiki.secondlife.com/wiki/LLSD).
 * @param {object} viewerData Object containing viewer related data. (grid ...)
 * @param {string} first First name of the avatar.
 * @param {string} last Last name of the avatar.
 * @param {string} password Hashed and salted password.
 */
async function loginWithLLSD (viewerData, first, last, password) {
  const loginData = {
    first,
    last,
    passwd: password,
    start: 'last',
    channel: viewerName,
    version: viewerVersion,
    platform: viewerPlatform,
    platform_version: viewerPlatformVersion,
    platform_string: window.navigator.userAgent,
    // mac and id0 will be added on the server side
    options: [
      'buddy-list',
      'inventory-root',
      'inventory-skeleton'
    ],
    agree_to_tos: true,
    read_critical: true,
    viewer_digest: '',
    last_exec_event: 0,
    last_exec_duration: 0,
    address_size: 32 // Is os 32 or 64 bit.
  }

  const response = await window.fetch('/api/login', {
    method: 'POST',
    body: JSON.stringify(loginData),
    headers: createProxyLoginHeaders(viewerData, true)
  })
  const body = await response.text()
  const parsed = LLSD.parse(response.headers.get('content-type').split(';')[0], body)

  // for transforming all UUIDs into strings
  const data = JSON.parse(JSON.stringify(parsed))
  data.andromedaSessionId = response.headers.get('x-andromeda-session-id')
  return data
}

// Logout an avatar
export function logout () {
  return (dispatch, getState, extra) => {
    const circuit = extra.circuit
    const activeState = getState()

    if (!selectIsLoggedIn(activeState)) {
      return Promise.reject(new Error("You aren't logged in!"))
    }

    return new Promise((resolve, reject) => {
      circuit.send('LogoutRequest', {
        AgentData: [
          {
            AgentID: selectAgentId(activeState),
            SessionID: selectSessionId(activeState)
          }
        ]
      }, true)
        .catch(reject)

      dispatch(startLogout())

      let isLoggedOut = false
      const logoutHandler = () => {
        if (isLoggedOut) return
        isLoggedOut = true

        setTimeout(() => {
          circuit.removeEventListener('packetReceived', console.log)
          circuit.removeEventListener('LogoutReply', logoutHandler)
        }, 0)
        dispatch(afterAvatarSessionEnds())

        dispatch(didLogout())

        resolve()
      }

      circuit.addEventListener('packetReceived', console.log)
      circuit.addEventListener('LogoutReply', logoutHandler, { once: true })
      setTimeout(logoutHandler, ms.seconds(30)) // timeout for LogoutReply
    })
  }
}

// Login to a sim. Is called on the login process and sim-change
function connectToSim (sessionInfo, circuit) {
  return async (dispatch, getState, extraArgs) => {
    const Circuit = circuit.default
    const circuitCode = sessionInfo.circuit_code

    const activeCircuit = new Circuit(
      sessionInfo.sim_ip,
      sessionInfo.sim_port,
      circuitCode,
      sessionInfo.andromedaSessionId
    )
    extraArgs.circuit = activeCircuit

    const sessionId = sessionInfo.session_id
    const agentId = sessionInfo.agent_id

    dispatch(connectCircuit()) // Connect message parsing with circuit.

    activeCircuit.addEventListener('KickUser', msg => dispatch(getKicked(msg.detail)))

    await activeCircuit.send('UseCircuitCode', {
      CircuitCode: [
        {
          Code: circuitCode,
          SessionID: sessionId,
          ID: agentId
        }
      ]
    }, true)

    await activeCircuit.send('CompleteAgentMovement', {
      AgentData: [
        {
          AgentID: agentId,
          SessionID: sessionId,
          CircuitCode: circuitCode
        }
      ]
    }, true)

    await activeCircuit.send('AgentUpdate', {
      AgentData: [
        {
          AgentID: agentId,
          SessionID: sessionId,
          BodyRotation: [0, 0, 0],
          HeadRotation: [0, 0, 0],
          State: 0,
          CameraCenter: [0, 0, 0],
          CameraAtAxis: [0, 0, 0],
          CameraLeftAxis: [0, 0, 0],
          CameraUpAxis: [0, 0, 0],
          Far: 0,
          ControlFlags: 0,
          Flags: 0
        }
      ]
    }, true)

    activeCircuit.send('UUIDNameRequest', {
      UUIDNameBlock: [
        {
          ID: agentId
        }
      ]
    }, true)

    setTimeout(function () {
      activeCircuit.send('RequestRegionInfo', {
        AgentData: [
          {
            AgentID: agentId,
            SessionID: sessionId
          }
        ]
      }, true)

      dispatch(requestAvatarProperties(agentId))

      dispatch(retrieveInstantMessages())
    }, 100)
  }
}

function getKicked (msg) {
  return (dispatch, getState, extra) => {
    const activeState = getState()
    const agentId = selectAgentId(activeState)
    const sessionId = selectSessionId(activeState)
    const msgAgentId = getValueOf(msg, 'UserInfo', 0, 'AgentID')
    const msgSessionId = getValueOf(msg, 'UserInfo', 0, 'SessionID')

    if (agentId === msgAgentId && sessionId === msgSessionId) {
      dispatch(afterAvatarSessionEnds())

      dispatch(userWasKicked({
        reason: getStringValueOf(msg, 'UserInfo', 0, 'Reason')
      }))
    }
  }
}

// Cleanup thats happens after logout and getting kicked
function afterAvatarSessionEnds () {
  return (dispatch, getState, extra) => {
    extra.circuit.close()
    extra.circuit = null

    for (const cb of extra.onAvatarLogout || []) {
      cb()
    }
    extra.onAvatarLogout = []

    return dispatch(deleteOldLocalChat())
  }
}

function requestAvatarProperties (avatarID) {
  return (dispatch, getState, { circuit }) => {
    const activeState = getState()
    const agentID = selectAgentId(activeState)
    const sessionID = selectSessionId(activeState)

    circuit.send('AvatarPropertiesRequest', {
      AgentData: [
        {
          AgentID: agentID,
          SessionID: sessionID,
          AvatarID: avatarID
        }
      ]
    }, true)
  }
}
