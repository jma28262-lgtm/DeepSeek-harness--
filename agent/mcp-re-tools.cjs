#!/usr/bin/env node
/**
 * re-tools MCP server — zero-dependency Model Context Protocol server (stdio).
 *
 * Provides:
 *   reverse engineering: hexdump, strings, pe_info, elf_info
 *   local model control: local_models (list), local_chat (OpenAI-compatible
 *     chat completion against a local llama.cpp server)
 *
 * Protocol: MCP stdio transport, newline-delimited JSON-RPC 2.0.
 * No external dependencies: plain Node.js built-ins only.
 */
'use strict'

const fs = require('node:fs')
const readline = require('node:readline')

const SERVER_NAME = 're-tools'
const SERVER_VERSION = '1.0.0'
const PROTOCOL_VERSION = '2024-11-05'
const MAX_FILE = 32 * 1024 * 1024
const LLM_BASE = process.env.RE_TOOLS_LLM_BASE || 'http://127.0.0.1:11435/v1'
const LLM_MODEL = process.env.RE_TOOLS_LLM_MODEL || ''

// ── byte helpers ────────────────────────────────────────────────────────────
const u16le = (b, o) => b[o] | (b[o + 1] << 8)
const u16be = (b, o) => (b[o] << 8) | b[o + 1]
const u32le = (b, o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)
const u32be = (b, o) => ((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]) >>> 0
const u64le = (b, o) => {
  const lo = u32le(b, o); const hi = u32le(b, o + 4)
  return hi * 4294967296 + lo
}
const u64be = (b, o) => {
  const lo = u32be(b, o + 4); const hi = u32be(b, o)
  return hi * 4294967296 + lo
}
const cstr = (b, o, max = 512) => {
  const end = Math.min(b.length, o + max)
  let s = ''
  for (let i = o; i < end; i++) {
    if (b[i] === 0) break
    s += String.fromCharCode(b[i])
  }
  return s
}
const hex = (v, w = 2) => v.toString(16).padStart(w, '0')

function readFileBytes(path) {
  const st = fs.statSync(path)
  if (st.size > MAX_FILE) {
    throw new Error(`file is ${st.size} bytes, exceeds the ${MAX_FILE}-byte limit`)
  }
  return fs.readFileSync(path)
}

// ── hexdump ─────────────────────────────────────────────────────────────────
function formatHexdump(bytes, baseOffset) {
  let out = ''
  for (let i = 0; i < bytes.length; i += 16) {
    const row = bytes.subarray(i, i + 16)
    const addr = baseOffset + i
    const hexPart = []
    const asciiPart = []
    for (let j = 0; j < 16; j++) {
      if (j < row.length) {
        hexPart.push(hex(row[j]))
        const c = row[j]
        asciiPart.push(c >= 0x20 && c < 0x7f ? String.fromCharCode(c) : '.')
      } else {
        hexPart.push('  ')
        asciiPart.push(' ')
      }
      if (j === 7) hexPart.push('')
    }
    out += addr.toString(16).padStart(8, '0') + '  ' + hexPart.join(' ') + '  |' + asciiPart.join('') + '|\n'
  }
  return out
}

// ── strings ─────────────────────────────────────────────────────────────────
function extractStrings(bytes, minLen, utf16) {
  const out = []
  let cur = []
  let curStart = 0
  const flush = () => {
    if (cur.length >= minLen) out.push({ offset: curStart, text: String.fromCharCode(...cur) })
    cur = []
  }
  if (utf16) {
    for (let i = 0; i + 1 < bytes.length; i += 2) {
      const c = bytes[i] | (bytes[i + 1] << 8)
      if ((c >= 0x20 && c < 0x7f) || c === 0x0a || c === 0x0d || c === 0x09) {
        if (cur.length === 0) curStart = i
        cur.push(c)
      } else flush()
    }
    flush()
  } else {
    for (let i = 0; i < bytes.length; i++) {
      const c = bytes[i]
      if ((c >= 0x20 && c < 0x7f) || c === 0x0a || c === 0x0d || c === 0x09) {
        if (cur.length === 0) curStart = i
        cur.push(c)
      } else flush()
    }
    flush()
  }
  return out
}

// ── PE parsing ──────────────────────────────────────────────────────────────
function parsePe(b) {
  if (b.length < 0x40) return 'file too small for a DOS header'
  if (b[0] !== 0x4d || b[1] !== 0x5a) return 'not a PE file (missing MZ signature)'
  const e_lfanew = u32le(b, 0x3c)
  if (e_lfanew + 24 > b.length) return 'truncated: e_lfanew out of range'
  if (b[e_lfanew] !== 0x50 || b[e_lfanew + 1] !== 0x45 || b[e_lfanew + 2] !== 0 || b[e_lfanew + 3] !== 0) {
    return 'missing PE signature at e_lfanew ' + e_lfanew
  }
  const coff = e_lfanew + 4
  const machine = u16le(b, coff)
  const nSections = u16le(b, coff + 2)
  const timeDate = u32le(b, coff + 4)
  const sizeOpt = u16le(b, coff + 16)
  const chars = u16le(b, coff + 18)
  const opt = coff + 20
  const magic = u16le(b, opt)
  const is32 = magic === 0x10b
  const is64 = magic === 0x20b
  if (!is32 && !is64) return 'unknown optional header magic 0x' + hex(magic, 4)
  const entry = u32le(b, opt + 16)
  const imageBase = is32 ? u32le(b, opt + 28) : u64le(b, opt + 24)
  const sectionAlign = u32le(b, opt + 32)
  const fileAlign = u32le(b, opt + 36)
  const subsystem = u16le(b, opt + 68)
  const dllChars = u16le(b, opt + 70)
  const ddOffset = opt + (is32 ? 96 : 112)
  const nDirs = u32le(b, opt + (is32 ? 92 : 108))
  const importRva = ddOffset + 8 <= b.length ? u32le(b, ddOffset + 8) : 0
  const importSize = ddOffset + 12 <= b.length ? u32le(b, ddOffset + 12) : 0
  const exportRva = ddOffset <= b.length ? u32le(b, ddOffset) : 0
  const exportSize = ddOffset + 4 <= b.length ? u32le(b, ddOffset + 4) : 0

  const sections = []
  const secStart = opt + sizeOpt
  const sectionSize = 40
  const machineNames = { 0x14c: 'x86', 0x8664: 'x64', 0x1c0: 'ARM', 0xaa64: 'ARM64', 0x1c4: 'ARMv7', 0x200: 'IA64' }
  const subNames = { 1: 'NATIVE', 2: 'WINDOWS_GUI', 3: 'WINDOWS_CUI', 7: 'POSIX_CUI', 9: 'WINDOWS_CE_GUI' }
  for (let i = 0; i < nSections; i++) {
    const off = secStart + i * sectionSize
    if (off + 40 > b.length) break
    const name = cstr(b, off, 8)
    const vsize = u32le(b, off + 8)
    const vaddr = u32le(b, off + 12)
    const rsize = u32le(b, off + 16)
    const roff = u32le(b, off + 20)
    const secChars = u32le(b, off + 36)
    const flags = []
    if (secChars & 0x20) flags.push('CODE')
    if (secChars & 0x40) flags.push('IDATA')
    if (secChars & 0x80) flags.push('UDATA')
    if (secChars & 0x20000000) flags.push('EXEC')
    if (secChars & 0x40000000) flags.push('READ')
    if (secChars & 0x80000000) flags.push('WRITE')
    sections.push({ name, vsize, vaddr, rsize, roff, flags: flags.join('|') || hex(secChars, 8) })
  }

  const rvaToOff = (rva) => {
    for (const s of sections) {
      const svs = Math.max(s.vsize, s.rsize)
      if (rva >= s.vaddr && rva < s.vaddr + svs) return s.roff + (rva - s.vaddr)
    }
    return -1
  }

  const imports = []
  if (importRva) {
    let off = rvaToOff(importRva)
    let guard = 0
    while (off >= 0 && off + 20 <= b.length && guard < 256) {
      const nameRva = u32le(b, off + 12)
      if (nameRva === 0) break
      const noff = rvaToOff(nameRva)
      const dllName = noff >= 0 ? cstr(b, noff, 256) : '<unresolved rva ' + nameRva + '>'
      imports.push(dllName)
      off += 20
      guard++
    }
  }
  const exports = []
  if (exportRva) {
    const eoff = rvaToOff(exportRva)
    if (eoff >= 0 && eoff + 40 <= b.length) {
      const nNames = u32le(b, eoff + 24)
      const namesRva = u32le(b, eoff + 32)
      const noff = rvaToOff(namesRva)
      for (let i = 0; i < Math.min(nNames, 64); i++) {
        const p = noff + i * 4
        if (p + 4 > b.length) break
        const nameRva = u32le(b, p)
        const fo = rvaToOff(nameRva)
        exports.push(fo >= 0 ? cstr(b, fo, 128) : '<rva ' + nameRva + '>')
      }
    }
  }

  let out = 'PE ' + (is32 ? '32' : '32+') + ' (' + (machineNames[machine] || 'machine 0x' + hex(machine, 4)) + ')\n'
  out += 'entry: 0x' + hex(entry, 8) + '  imageBase: 0x' + imageBase.toString(16) + '\n'
  out += 'sections: ' + nSections + '  alignment: ' + sectionAlign + '/' + fileAlign + '  subsystem: ' + (subNames[subsystem] || subsystem) + '\n'
  out += 'dllCharacteristics: 0x' + hex(dllChars, 4) + '  timestamp: ' + timeDate + '\n'
  out += 'dataDirs: ' + nDirs + '  export: ' + (exportSize ? '0x' + hex(exportRva, 8) + ' (' + exportSize + 'b)' : '-') + '  import: ' + (importSize ? '0x' + hex(importRva, 8) + ' (' + importSize + 'b)' : '-') + '\n\n'
  out += 'sections:\n'
  out += 'name       vsize     vaddr      rawsize   rawoff    flags\n'
  for (const s of sections) {
    out += s.name.padEnd(10) + ' 0x' + hex(s.vsize, 8) + ' 0x' + hex(s.vaddr, 8) + ' 0x' + hex(s.rsize, 8) + ' 0x' + hex(s.roff, 8) + '  ' + s.flags + '\n'
  }
  if (imports.length) out += '\nimports:\n  ' + imports.join('\n  ') + '\n'
  if (exports.length) out += '\nexports:\n  ' + exports.join('\n  ') + '\n'
  return out
}

// ── ELF parsing ─────────────────────────────────────────────────────────────
function parseElf(b) {
  if (b.length < 16) return 'file too small for an ELF header'
  if (b[0] !== 0x7f || b[1] !== 0x45 || b[2] !== 0x4c || b[3] !== 0x46) return 'not an ELF file (missing \\x7fELF)'
  const is64 = b[4] === 2
  const isLE = b[5] === 1
  if (b[4] !== 1 && b[4] !== 2) return 'unknown ELF class ' + b[4]
  if (b[5] !== 1 && b[5] !== 2) return 'unknown ELF data encoding ' + b[5]
  const u16 = isLE ? u16le : u16be
  const u32 = isLE ? u32le : u32be
  const u64 = isLE ? u64le : u64be
  const e_type = u16(b, 16)
  const e_machine = u16(b, 18)
  const e_entry = is64 ? u64(b, 24) : u32(b, 24)
  const e_phoff = is64 ? u64(b, 32) : u32(b, 28)
  const e_shoff = is64 ? u64(b, 40) : u32(b, 32)
  const e_phentsize = u16(b, is64 ? 54 : 42)
  const e_phnum = u16(b, is64 ? 56 : 44)
  const e_shentsize = u16(b, is64 ? 58 : 46)
  const e_shnum = u16(b, is64 ? 60 : 48)
  const e_shstrndx = u16(b, is64 ? 62 : 50)
  const types = { 0: 'NONE', 1: 'REL', 2: 'EXEC', 3: 'DYN', 4: 'CORE' }
  const machines = { 3: 'x86', 40: 'ARM', 62: 'x86-64', 183: 'AArch64', 20: 'PowerPC', 21: 'PowerPC64', 8: 'MIPS', 243: 'RISC-V' }
  const pTypes = { 0: 'NULL', 1: 'LOAD', 2: 'DYNAMIC', 3: 'INTERP', 4: 'NOTE', 6: 'PHDR', 7: 'TLS', 0x6474e550: 'GNU_EH_FRAME', 0x6474e551: 'GNU_STACK', 0x6474e552: 'GNU_RELRO' }
  let out = 'ELF' + (is64 ? '64' : '32') + ' ' + (isLE ? 'LSB' : 'MSB') + ' ' + (types[e_type] || 'type ' + e_type) + ' (' + (machines[e_machine] || 'machine ' + e_machine) + ')\n'
  out += 'entry: 0x' + e_entry.toString(16) + '  ph: ' + e_phnum + '  sh: ' + e_shnum + '\n\n'
  if (e_phoff && e_phnum) {
    out += 'program headers:\n'
    out += 'type       offset     vaddr      filesz     memsz      flags\n'
    for (let i = 0; i < e_phnum; i++) {
      const o = e_phoff + i * e_phentsize
      if (o + e_phentsize > b.length) break
      const pType = u32(b, o)
      const offset = is64 ? u64(b, o + 8) : u32(b, o + 4)
      const vaddr = is64 ? u64(b, o + 16) : u32(b, o + 8)
      const filesz = is64 ? u64(b, o + 32) : u32(b, o + 16)
      const memsz = is64 ? u64(b, o + 40) : u32(b, o + 20)
      const flags = is64 ? u32(b, o + 4) : u32(b, o + 24)
      const fl = (flags & 1 ? 'R' : '-') + (flags & 2 ? 'W' : '-') + (flags & 4 ? 'X' : '-')
      out += (pTypes[pType] || '0x' + pType.toString(16)).padEnd(12) + ' 0x' + hex(offset, 8) + ' 0x' + hex(vaddr, 8) + ' 0x' + hex(filesz, 8) + ' 0x' + hex(memsz, 8) + '  ' + fl + '\n'
    }
  }
  if (e_shoff && e_shnum) {
    out += '\nsection headers (' + e_shnum + '):\n'
    out += 'name       addr       offset     size       type\n'
    for (let i = 0; i < Math.min(e_shnum, 96); i++) {
      const o = e_shoff + i * e_shentsize
      if (o + e_shentsize > b.length) break
      const nameOff = u32(b, o)
      const sType = u32(b, o + 4)
      const addr = is64 ? u64(b, o + 16) : u32(b, o + 12)
      const offset = is64 ? u64(b, o + 24) : u32(b, o + 16)
      const size = is64 ? u64(b, o + 32) : u32(b, o + 20)
      const name = nameOff ? cstr(b, nameOff, 32) : ''
      out += (name || '(null)').padEnd(10) + ' 0x' + hex(addr, 8) + ' 0x' + hex(offset, 8) + ' 0x' + hex(size, 8) + '  ' + sType + '\n'
    }
  }
  return out
}

// ── local model control ─────────────────────────────────────────────────────
async function listLocalModels() {
  const res = await fetch(LLM_BASE + '/models', { signal: AbortSignal.timeout(15000) })
  if (!res.ok) throw new Error('local model endpoint returned HTTP ' + res.status)
  const data = await res.json()
  const models = (data.data || data.models || []).map((m) => m.id || m.name || JSON.stringify(m))
  if (!models.length) return 'no models advertised by ' + LLM_BASE
  return models.map((m) => '- ' + m).join('\n')
}

async function chatLocal(args) {
  const model = args.model || LLM_MODEL
  if (!model) throw new Error('no model specified and RE_TOOLS_LLM_MODEL is unset')
  const messages = []
  if (args.system) messages.push({ role: 'system', content: args.system })
  messages.push({ role: 'user', content: args.prompt })
  const body = {
    model,
    messages,
    stream: false,
    ...(args.temperature != null ? { temperature: args.temperature } : {}),
    ...(args.max_tokens != null ? { max_tokens: args.max_tokens } : {})
  }
  const res = await fetch(LLM_BASE + '/chat/completions', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
    signal: AbortSignal.timeout(args.timeout_ms || 120000)
  })
  if (!res.ok) throw new Error('chat completion failed: HTTP ' + res.status + ' ' + (await res.text()).slice(0, 500))
  const data = await res.json()
  const text = data.choices?.[0]?.message?.content ?? JSON.stringify(data)
  return String(text)
}

// ── MCP protocol ────────────────────────────────────────────────────────────
const TOOLS = [
  {
    name: 'hexdump',
    description: 'Dump a binary file as hexadecimal bytes with an ASCII column, for reverse-engineering inspection. Works on files up to 32 MiB.',
    inputSchema: {
      type: 'object',
      properties: {
        file_path: { type: 'string', description: 'Path to the binary file to inspect.' },
        offset: { type: 'number', description: 'Byte offset to start from. Defaults to 0.' },
        length: { type: 'number', description: 'Number of bytes to dump. Defaults to 256, capped at 4096.' }
      },
      required: ['file_path']
    }
  },
  {
    name: 'strings',
    description: 'Extract printable strings from a binary file (ASCII or UTF-16LE), useful for probing executables, firmware and dumps. Works on files up to 32 MiB.',
    inputSchema: {
      type: 'object',
      properties: {
        file_path: { type: 'string', description: 'Path to the binary file.' },
        min_length: { type: 'number', description: 'Minimum string length. Defaults to 4.' },
        encoding: { type: 'string', enum: ['ascii', 'utf16le'], description: 'String encoding. Defaults to ascii.' },
        limit: { type: 'number', description: 'Maximum number of strings to return. Defaults to 200.' }
      },
      required: ['file_path']
    }
  },
  {
    name: 'pe_info',
    description: 'Parse a Windows PE/PE32+ executable header: architecture, entry point, sections, imports and exports. Works on files up to 32 MiB.',
    inputSchema: {
      type: 'object',
      properties: {
        file_path: { type: 'string', description: 'Path to the PE file (.exe/.dll).' }
      },
      required: ['file_path']
    }
  },
  {
    name: 'elf_info',
    description: 'Parse an ELF (Executable and Linkable Format) binary header: class, endianness, entry point, program and section headers. Works on files up to 32 MiB.',
    inputSchema: {
      type: 'object',
      properties: {
        file_path: { type: 'string', description: 'Path to the ELF file.' }
      },
      required: ['file_path']
    }
  },
  {
    name: 'local_models',
    description: 'List models advertised by the local model server (llama.cpp OpenAI-compatible endpoint).',
    inputSchema: { type: 'object', properties: {} }
  },
  {
    name: 'local_chat',
    description: 'Run a chat completion against the local model server (llama.cpp OpenAI-compatible endpoint).',
    inputSchema: {
      type: 'object',
      properties: {
        prompt: { type: 'string', description: 'The user prompt to send.' },
        system: { type: 'string', description: 'Optional system prompt.' },
        model: { type: 'string', description: 'Model id; defaults to RE_TOOLS_LLM_MODEL.' },
        temperature: { type: 'number', description: 'Sampling temperature.' },
        max_tokens: { type: 'number', description: 'Maximum tokens to generate.' },
        timeout_ms: { type: 'number', description: 'Request timeout in ms. Defaults to 120000.' }
      },
      required: ['prompt']
    }
  }
]

async function callTool(name, args) {
  switch (name) {
    case 'hexdump': {
      const bytes = readFileBytes(args.file_path)
      const start = args.offset ?? 0
      const len = Math.min(4096, args.length ?? 256)
      const end = Math.min(bytes.length, start + len)
      if (start >= bytes.length) return 'offset ' + start + ' is past end of file (' + bytes.length + ' bytes)'
      return formatHexdump(bytes.subarray(start, end), start)
    }
    case 'strings': {
      const minLen = Math.max(1, args.min_length ?? 4)
      const utf16 = (args.encoding ?? 'ascii') === 'utf16le'
      const bytes = readFileBytes(args.file_path)
      const found = extractStrings(bytes, minLen, utf16)
      const limit = Math.min(500, args.limit ?? 200)
      let out = found.length + ' strings found (showing up to ' + limit + '):\n'
      for (const s of found.slice(0, limit)) {
        out += '0x' + s.offset.toString(16).padStart(8, '0') + ': ' + s.text + '\n'
      }
      return out
    }
    case 'pe_info':
      return parsePe(readFileBytes(args.file_path))
    case 'elf_info':
      return parseElf(readFileBytes(args.file_path))
    case 'local_models':
      return await listLocalModels()
    case 'local_chat':
      return await chatLocal(args)
    default:
      throw new Error('unknown tool: ' + name)
  }
}

const rl = readline.createInterface({ input: process.stdin, terminal: false })

function send(msg) {
  process.stdout.write(JSON.stringify(msg) + '\n')
}

rl.on('line', (line) => {
  if (!line.trim()) return
  let msg
  try {
    msg = JSON.parse(line)
  } catch {
    return
  }
  const { id, method, params } = msg

  // notifications carry no id
  if (method === 'notifications/initialized') return
  if (method === 'ping') {
    if (id !== undefined) send({ jsonrpc: '2.0', id, result: {} })
    return
  }
  if (method === 'initialize') {
    send({
      jsonrpc: '2.0',
      id,
      result: {
        protocolVersion: PROTOCOL_VERSION,
        capabilities: { tools: { listChanged: false } },
        serverInfo: { name: SERVER_NAME, version: SERVER_VERSION }
      }
    })
    return
  }
  if (method === 'tools/list') {
    send({ jsonrpc: '2.0', id, result: { tools: TOOLS } })
    return
  }
  if (method === 'tools/call') {
    const name = params?.name
    const args = params?.arguments ?? {}
    callTool(name, args)
      .then((text) => {
        send({ jsonrpc: '2.0', id, result: { content: [{ type: 'text', text }] } })
      })
      .catch((error) => {
        send({
          jsonrpc: '2.0',
          id,
          result: {
            content: [{ type: 'text', text: name + ' failed: ' + error.message }],
            isError: true
          }
        })
      })
    return
  }
  if (method === 'tools/list_changed') return
  // unknown request → protocol error
  if (id !== undefined) {
    send({ jsonrpc: '2.0', id, error: { code: -32601, message: 'method not found: ' + method } })
  }
})

process.stderr.write('[re-tools] MCP server ready\n')
