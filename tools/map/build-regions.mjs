// 행정구역 경계 데이터를 게임 지도용으로 가공한다.
//
//   cd tools/map
//   npm install        (처음 한 번)
//   npm run build
//
// 하는 일
//   1. 행정동 경계 원자료를 내려받는다 (.cache, git에 넣지 않음). 해시가 다르면 멈춘다.
//   2. 행정동을 시·군·구로 합친다. 구가 있는 시(수원시 장안구 등)는 시 하나로 합친다.
//   3. 게임 지도에 맞게 단순화하고, 아주 작은 섬을 뺀다 (독도는 남긴다).
//   4. 평면 좌표로 바꾸고 삼각형으로 나눠 Unity가 바로 읽는 JSON으로 쓴다.
//   5. 서버 쪽(모의 서버)이 쓰는 지역 목록을 C# 파일로 쓴다.
//
// 원자료와 이용 조건
//   통계청 통계지리정보서비스(SGIS)가 공공누리 제1유형(출처표시)으로 개방한 행정동 경계를
//   vuski/admdongkor 가 가공해 CC BY 4.0 으로 배포한 것. 두 조건 모두 출처를 표시하면
//   상업적 이용과 변형이 가능하다. 출처 문구는 결과 JSON의 attribution 에 넣고 지도 화면에 표시한다.
//   https://github.com/vuski/admdongkor/blob/master/LICENSE-DATA

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import mapshaper from 'mapshaper';
import earcut from 'earcut';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const CACHE = path.join(HERE, '.cache');
const CLIENT = path.resolve(HERE, '../../client/Assets/CityGuardians');
const OUT_JSON = path.join(CLIENT, 'Resources/Data/kr_regions.json');
const OUT_CS = path.join(CLIENT, 'Domain/Generated/KrRegions.g.cs');

const SOURCE = {
  version: 'ver20261001',
  baseDate: '2026-10-01',
  commit: '3bc00489b76696db864bdbea4b19985cdfa8b403',
  file: 'HangJeongDong_ver20261001.geojson',
  sha256: '843ece3a0b3429742cd4a1b699cc13987a88b5e5401f382e7fdaaa194ea5e605',
};
SOURCE.url = `https://raw.githubusercontent.com/vuski/admdongkor/${SOURCE.commit}/${SOURCE.version}/${SOURCE.file}`;

const ATTRIBUTION =
  '지도 경계: 통계청 통계지리정보서비스(SGIS) 행정동 경계(공공누리 제1유형)를 vuski/admdongkor 가 가공(CC BY 4.0)한 ' +
  `${SOURCE.version} 자료를 시·군·구로 합치고 단순화함`;

// ── 조정값 ─────────────────────────────────────────────
const SIMPLIFY = '6%';          // 남기는 점의 비율. 크게 하면 정밀해지고 파일이 커진다.
const MIN_ISLAND_KM2 = 1.5;     // 이보다 작은 섬은 뺀다 (지역에서 가장 큰 땅과 독도는 남긴다).
const MIN_HOLE_KM2 = 1.0;       // 이보다 작은 구멍은 메운다.
const UNIT_DEG = 0.0005;        // 좌표 한 칸 = 위도 0.0005도 (약 56 m)

// 평면 좌표: 한반도 남쪽 가운데 위도에서 동서 길이를 맞춘 단순 투영. 게임 지도에는 충분하다.
const LON0 = 127.5, LAT0 = 36.0;
const KX = Math.cos(LAT0 * Math.PI / 180);
const KM_PER_DEG = 111.32;

// 구가 있는 시가 속한 도. 이 도의 "○○시△△구"는 시 하나로 합친다. (특별시·광역시의 자치구는 그대로 둔다.)
const PROVINCES_WITH_GENERAL_GU = ['41', '43', '44', '47', '48', '52'];

const SIDO_SHORT = {
  '11': '서울', '12': '전남광주', '26': '부산', '27': '대구', '28': '인천', '30': '대전', '31': '울산', '36': '세종',
  '41': '경기', '43': '충북', '44': '충남', '47': '경북', '48': '경남', '50': '제주', '51': '강원', '52': '전북',
};

// 원자료(2026-10-01 기준)에서 나와야 하는 수. 자료를 바꾸면 이 값도 확인해서 고친다.
// 시·군·구 256곳 중 구가 있는 시 13곳의 구 39곳을 시로 합치면 230곳이다.
// 운영 홈페이지에는 229곳으로 적혀 있었다. 운영 DB를 보기 전에는 차이 1곳의 원인을 알 수 없다
// (docs/MAP_SYSTEM.md 2장).
const EXPECTED_SIDOS = 16;
const EXPECTED_REGIONS = 230;

// 본토에서 멀리 떨어진 섬 지역 (울릉군, 옹진군). 지도에는 그대로 그리고 고를 수도 있지만,
// 시·도를 눌러 확대할 때의 범위에서는 뺀다. 화면을 끌거나 축소하면 보인다.
const FAR_ISLAND_REGIONS = new Set(['47940', '28720']);

/** 독도: 울릉군에 속하고 울릉도보다 훨씬 동쪽에 있는 땅. 아무리 작아도 지도에서 빼지 않는다. */
const ULLEUNG = '47940';
const isDokdo = (regionCode, lonLatRing) => regionCode === ULLEUNG && lonLatRing[0][0] > 131.5;

function polygonsOf(geometry) {
  if (!geometry) return [];
  if (geometry.type === 'Polygon') return [geometry.coordinates];
  if (geometry.type === 'MultiPolygon') return geometry.coordinates;
  throw new Error('다룰 수 없는 도형: ' + geometry.type);
}

// ── 1. 원자료 ───────────────────────────────────────────
async function ensureSource() {
  fs.mkdirSync(CACHE, { recursive: true });
  const file = path.join(CACHE, SOURCE.file);
  if (!fs.existsSync(file)) {
    console.log('내려받는 중:', SOURCE.url);
    const response = await fetch(SOURCE.url);
    if (!response.ok) throw new Error(`내려받기 실패: HTTP ${response.status}`);
    fs.writeFileSync(file, Buffer.from(await response.arrayBuffer()));
  }
  const hash = crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
  if (hash !== SOURCE.sha256) throw new Error(`원자료 해시가 다릅니다: ${hash}\n기대값: ${SOURCE.sha256}`);
  return file;
}

// ── 2~3. 합치기와 단순화 (mapshaper) ────────────────────
async function dissolveAndSimplify(sourceFile) {
  const dongs = JSON.parse(fs.readFileSync(sourceFile, 'utf8'));

  // 독도는 단순화 단계에서 사라진다 (mapshaper 는 지역마다 가장 큰 땅만 지켜 준다).
  // 원자료의 도형을 따로 떼어 두었다가 단순화하지 않고 그대로 넣는다.
  const dokdo = [];
  for (const feature of dongs.features) {
    if (feature.properties.sgg !== ULLEUNG) continue;
    for (const polygon of polygonsOf(feature.geometry)) if (isDokdo(ULLEUNG, polygon[0])) dokdo.push(polygon);
  }
  if (dokdo.length === 0) throw new Error('원자료에서 독도를 찾지 못했습니다');

  // 행정동마다 "어느 시·군·구로 합칠지"를 먼저 정해 둔다.
  for (const feature of dongs.features) {
    const p = feature.properties;
    const match = /^(.+?시)(.+구)$/.exec(p.sggnm);
    const general = !!match && PROVINCES_WITH_GENERAL_GU.includes(p.sido);
    feature.properties = {
      code: general ? p.sgg.slice(0, 4) + '0' : p.sgg,
      name: general ? match[1] : p.sggnm,
      sido: p.sido,
      sidonm: p.sidonm,
    };
  }
  // 합친 코드가 원래 있던 다른 시·군·구의 코드와 겹치면 안 된다.
  const names = new Map();
  for (const { properties: p } of dongs.features) {
    const known = names.get(p.code);
    if (known && known !== p.sidonm + ' ' + p.name) throw new Error(`코드 ${p.code} 가 겹칩니다: ${known} / ${p.sidonm} ${p.name}`);
    names.set(p.code, p.sidonm + ' ' + p.name);
  }

  const commands = [
    `-i dongs.json`,
    `-dissolve code copy-fields=name,sido,sidonm name=regions`,
    `-simplify weighted keep-shapes ${SIMPLIFY}`,
    `-clean`,
    `-o regions.json format=geojson precision=0.00001`,
    `-dissolve sido copy-fields=sidonm name=sidos`,
    `-o sidos.json format=geojson precision=0.00001`,
  ].join(' ');
  const output = await mapshaper.applyCommands(commands, { 'dongs.json': dongs });
  const regions = JSON.parse(output['regions.json'].toString());
  const sidos = JSON.parse(output['sidos.json'].toString());

  const ulleung = regions.features.find(f => f.properties.code === ULLEUNG);
  if (!ulleung) throw new Error('울릉군을 찾지 못했습니다');
  const kept = polygonsOf(ulleung.geometry).filter(polygon => !isDokdo(ULLEUNG, polygon[0]));
  ulleung.geometry = { type: 'MultiPolygon', coordinates: [...kept, ...dokdo] };
  return { regions, sidos };
}

// ── 4. 좌표 변환, 섬 걸러내기, 삼각형 ───────────────────
const project = ([lon, lat]) => [(lon - LON0) * KX / UNIT_DEG, (lat - LAT0) / UNIT_DEG];

function ringAreaKm2(ring) {
  let sum = 0;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++)
    sum += (ring[j][0] * ring[i][1] - ring[i][0] * ring[j][1]);
  const unitKm = UNIT_DEG * KM_PER_DEG;
  return Math.abs(sum) / 2 * unitKm * unitKm;
}

/** GeoJSON 고리 → 정수 좌표 고리. 닫는 점과 겹치는 점을 뺀다. 점이 3개 미만이면 null. */
function quantizeRing(coords) {
  const out = [];
  for (const point of coords) {
    const [x, y] = project(point);
    const q = [Math.round(x), Math.round(y)];
    const last = out[out.length - 1];
    if (!last || last[0] !== q[0] || last[1] !== q[1]) out.push(q);
  }
  if (out.length > 1 && out[0][0] === out[out.length - 1][0] && out[0][1] === out[out.length - 1][1]) out.pop();
  return out.length >= 3 ? out : null;
}

function keptPolygons(geometry, regionCode) {
  const polygons = polygonsOf(geometry).map(rings => {
    const outer = quantizeRing(rings[0]);
    return outer && { outer, area: ringAreaKm2(outer), rawHoles: rings.slice(1), dokdo: isDokdo(regionCode, rings[0]) };
  }).filter(Boolean);
  if (polygons.length === 0) return [];
  const largest = polygons.reduce((a, b) => (b.area > a.area ? b : a));
  return polygons
    .filter(p => p === largest || p.dokdo || p.area >= MIN_ISLAND_KM2)
    .map(p => ({
      ...p,
      holes: p.rawHoles.map(quantizeRing).filter(h => h && ringAreaKm2(h) >= MIN_HOLE_KM2),
    }))
    .sort((a, b) => b.area - a.area);
}

function pointInRing(x, y, ring) {
  let inside = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const [xi, yi] = ring[i], [xj, yj] = ring[j];
    if ((yi > y) !== (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
  }
  return inside;
}

function distanceToRings(x, y, rings) {
  let best = Infinity;
  for (const ring of rings) {
    for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
      const [ax, ay] = ring[j], [bx, by] = ring[i];
      const dx = bx - ax, dy = by - ay;
      const lengthSq = dx * dx + dy * dy;
      const t = lengthSq === 0 ? 0 : Math.max(0, Math.min(1, ((x - ax) * dx + (y - ay) * dy) / lengthSq));
      const px = ax + t * dx - x, py = ay + t * dy - y;
      best = Math.min(best, px * px + py * py);
    }
  }
  return Math.sqrt(best);
}

/** 이름표 자리: 도형 안에서 테두리와 가장 먼 점. 무게중심은 굽은 도형에서 바깥으로 나갈 수 있어 쓰지 않는다. */
function labelPoint(polygon) {
  const rings = [polygon.outer, ...polygon.holes];
  let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
  for (const [x, y] of polygon.outer) {
    minX = Math.min(minX, x); maxX = Math.max(maxX, x);
    minY = Math.min(minY, y); maxY = Math.max(maxY, y);
  }
  const inside = (x, y) => pointInRing(x, y, polygon.outer) && !polygon.holes.some(h => pointInRing(x, y, h));
  let best = null;
  const search = (x0, y0, x1, y1, steps) => {
    for (let i = 0; i <= steps; i++) {
      for (let j = 0; j <= steps; j++) {
        const x = x0 + (x1 - x0) * i / steps, y = y0 + (y1 - y0) * j / steps;
        if (!inside(x, y)) continue;
        const distance = distanceToRings(x, y, rings);
        if (!best || distance > best.distance) best = { x, y, distance };
      }
    }
  };
  search(minX, minY, maxX, maxY, 24);
  if (best) {
    const sx = (maxX - minX) / 24, sy = (maxY - minY) / 24;
    search(best.x - sx, best.y - sy, best.x + sx, best.y + sy, 10);
  }
  if (!best) best = { x: (minX + maxX) / 2, y: (minY + maxY) / 2, distance: 0 };
  return [Math.round(best.x), Math.round(best.y)];
}

function buildRegion(feature) {
  const { code, name, sido } = feature.properties;
  const polygons = keptPolygons(feature.geometry, code);
  if (polygons.length === 0) throw new Error(`도형이 없는 지역: ${code} ${name}`);

  const v = [], t = [], rings = [];
  let dokdo = null;
  for (const polygon of polygons) {
    const base = v.length / 2;
    const flat = [], holeStarts = [];
    for (const [x, y] of polygon.outer) flat.push(x, y);
    rings.push(base, polygon.outer.length);
    for (const hole of polygon.holes) {
      holeStarts.push(flat.length / 2);
      rings.push(base + flat.length / 2, hole.length);
      for (const [x, y] of hole) flat.push(x, y);
    }
    const triangles = earcut(flat, holeStarts);
    if (triangles.length === 0) {
      if (!polygon.dokdo) throw new Error(`삼각형으로 나누지 못함: ${code} ${name}`);
    }
    for (const index of triangles) t.push(base + index);
    v.push(...flat);
    if (polygon.dokdo && !dokdo) dokdo = labelPoint(polygon);
  }
  const [labelX, labelY] = labelPoint(polygons[0]);
  return {
    region: { code, sido, name, labelX, labelY, areaKm2: Math.round(polygons.reduce((s, p) => s + p.area, 0)), v, t, rings },
    dokdo,
  };
}

function buildSido(feature) {
  const { sido: code, sidonm: name } = feature.properties;
  const polygons = keptPolygons(feature.geometry, null);
  const lines = [];
  for (const polygon of polygons) {
    for (const ring of [polygon.outer, ...polygon.holes]) {
      lines.push(ring.length);
      for (const [x, y] of ring) lines.push(x, y);
    }
  }
  const [labelX, labelY] = labelPoint(polygons[0]);
  return { code, name, shortName: SIDO_SHORT[code], labelX, labelY, lines };
}

// ── 5. C# 지역 목록 ─────────────────────────────────────
function writeCSharp(sidos, regions) {
  const quote = s => '"' + s.replace(/\\/g, '\\\\').replace(/"/g, '\\"') + '"';
  const lines = [
    '// <auto-generated>',
    '// tools/map/build-regions.mjs 가 만든 파일입니다. 직접 고치지 마세요.',
    `// 기준: ${SOURCE.version} (${SOURCE.baseDate}). 시·도 ${sidos.length}곳, 시·군·구 ${regions.length}곳.`,
    '// 구가 있는 시(수원시 등)는 시 하나로 합쳤습니다. 코드는 원자료의 시·군·구 코드이며,',
    '// 운영 서버의 지역 코드와 같은지는 아직 확인하지 못했습니다 (docs/MAP_SYSTEM.md 2장).',
    '// </auto-generated>',
    'using System.Collections.Generic;',
    '',
    'namespace CityGuardians.Domain',
    '{',
    '    public static class KrRegions',
    '    {',
    `        public const string BaseDate = ${quote(SOURCE.baseDate)};`,
    '',
    '        public static List<SidoDef> CreateSidos() => new List<SidoDef>',
    '        {',
    ...sidos.map(s => `            new SidoDef { code = ${quote(s.code)}, name = ${quote(s.name)}, shortName = ${quote(s.shortName)} },`),
    '        };',
    '',
    '        public static List<RegionDef> CreateRegions() => new List<RegionDef>',
    '        {',
    ...regions.map(r => `            new RegionDef { code = ${quote(r.code)}, sidoCode = ${quote(r.sido)}, name = ${quote(r.name)} },`),
    '        };',
    '    }',
    '}',
    '',
  ];
  fs.mkdirSync(path.dirname(OUT_CS), { recursive: true });
  fs.writeFileSync(OUT_CS, lines.join('\n'), 'utf8');
}

// ── 실행 ────────────────────────────────────────────────
const sourceFile = await ensureSource();
const { regions: regionFeatures, sidos: sidoFeatures } = await dissolveAndSimplify(sourceFile);

const built = regionFeatures.features.map(buildRegion);
const regions = built.map(b => b.region).sort((a, b) => a.code.localeCompare(b.code));
const sidos = sidoFeatures.features.map(buildSido).sort((a, b) => a.code.localeCompare(b.code));
const marks = built.filter(b => b.dokdo).map(b => ({ name: '독도', x: b.dokdo[0], y: b.dokdo[1] }));

// 검증: 수가 맞는가, 코드가 겹치지 않는가, 모든 지역이 그려지는가
if (sidos.length !== EXPECTED_SIDOS) throw new Error(`시·도가 ${sidos.length}곳입니다 (기대 ${EXPECTED_SIDOS})`);
if (regions.length !== EXPECTED_REGIONS) throw new Error(`시·군·구가 ${regions.length}곳입니다 (기대 ${EXPECTED_REGIONS})`);
if (new Set(regions.map(r => r.code)).size !== regions.length) throw new Error('지역 코드가 겹칩니다');
for (const sido of sidos) if (!sido.shortName) throw new Error(`짧은 이름이 없는 시·도: ${sido.code} ${sido.name}`);
for (const region of regions) {
  if (region.t.length < 3) throw new Error(`삼각형이 없는 지역: ${region.code} ${region.name}`);
  if (!sidos.some(s => s.code === region.sido)) throw new Error(`시·도를 찾을 수 없는 지역: ${region.code}`);
}
if (marks.length !== 1) throw new Error(`독도 표시가 ${marks.length}개입니다 (기대 1)`);

// 왼쪽 아래가 (0,0)이 되도록 옮긴다. y는 북쪽이 크다.
let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
const scan = (x, y) => { minX = Math.min(minX, x); maxX = Math.max(maxX, x); minY = Math.min(minY, y); maxY = Math.max(maxY, y); };
for (const r of regions) for (let i = 0; i < r.v.length; i += 2) scan(r.v[i], r.v[i + 1]);
const margin = 40;
const shiftX = -minX + margin, shiftY = -minY + margin;
for (const r of regions) {
  for (let i = 0; i < r.v.length; i += 2) { r.v[i] += shiftX; r.v[i + 1] += shiftY; }
  r.labelX += shiftX; r.labelY += shiftY;
  r.minX = Infinity; r.minY = Infinity; r.maxX = -Infinity; r.maxY = -Infinity;
  for (let i = 0; i < r.v.length; i += 2) {
    r.minX = Math.min(r.minX, r.v[i]); r.maxX = Math.max(r.maxX, r.v[i]);
    r.minY = Math.min(r.minY, r.v[i + 1]); r.maxY = Math.max(r.maxY, r.v[i + 1]);
  }
}
for (const s of sidos) {
  for (let i = 0; i < s.lines.length;) {
    const count = s.lines[i++];
    for (let k = 0; k < count; k++, i += 2) { s.lines[i] += shiftX; s.lines[i + 1] += shiftY; }
  }
  s.labelX += shiftX; s.labelY += shiftY;
  const own = regions.filter(r => r.sido === s.code);
  s.minX = Math.min(...own.map(r => r.minX)); s.maxX = Math.max(...own.map(r => r.maxX));
  s.minY = Math.min(...own.map(r => r.minY)); s.maxY = Math.max(...own.map(r => r.maxY));
  // 시·도를 눌렀을 때 화면에 맞추는 범위. 먼 섬 지역까지 넣으면 본토가 너무 작아진다.
  const near = own.filter(r => !FAR_ISLAND_REGIONS.has(r.code));
  s.focusMinX = Math.min(...near.map(r => r.minX)); s.focusMaxX = Math.max(...near.map(r => r.maxX));
  s.focusMinY = Math.min(...near.map(r => r.minY)); s.focusMaxY = Math.max(...near.map(r => r.maxY));
}
for (const m of marks) { m.x += shiftX; m.y += shiftY; }

const output = {
  attribution: ATTRIBUTION,
  baseDate: SOURCE.baseDate,
  width: Math.round(maxX - minX + margin * 2),
  height: Math.round(maxY - minY + margin * 2),
  unitKm: +(UNIT_DEG * KM_PER_DEG).toFixed(4),
  sidos,
  regions,
  marks,
};
fs.mkdirSync(path.dirname(OUT_JSON), { recursive: true });
fs.writeFileSync(OUT_JSON, JSON.stringify(output), 'utf8');
writeCSharp(sidos, regions);

const vertexCount = regions.reduce((s, r) => s + r.v.length / 2, 0);
const triangleCount = regions.reduce((s, r) => s + r.t.length / 3, 0);
const ringCount = regions.reduce((s, r) => s + r.rings.length / 2, 0);
const maxVerts = regions.reduce((a, b) => (b.v.length > a.v.length ? b : a));
console.log(`시·도 ${sidos.length}, 시·군·구 ${regions.length}`);
console.log(`점 ${vertexCount}, 삼각형 ${triangleCount}, 고리 ${ringCount}`);
console.log(`가장 복잡한 지역: ${maxVerts.name} (점 ${maxVerts.v.length / 2})`);
console.log(`지도 크기 ${output.width} x ${output.height} 칸 (1칸 = ${output.unitKm} km)`);
console.log(`독도 표시: (${marks[0].x}, ${marks[0].y})`);
console.log(`${path.relative(process.cwd(), OUT_JSON)}: ${(fs.statSync(OUT_JSON).size / 1024).toFixed(0)} KB`);
console.log(`${path.relative(process.cwd(), OUT_CS)}`);
