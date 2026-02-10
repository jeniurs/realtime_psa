
import pylsl

print("========================================")
print("LSL Stream Monitor")
print("========================================\n")

print("LSL streams를 검색 중... (최대 10초 대기)")
streams = pylsl.resolve_streams(wait_time=10.0)

if len(streams) == 0:
    print("❌ LSL stream을 찾을 수 없습니다.")
    print("\n확인 사항:")
    print("1. LSL stream이 실행 중인지 확인")
    print("2. 방화벽이 LSL 포트를 막고 있지 않은지 확인")
    print("3. 네트워크 설정 확인")
else:
    print(f"✅ {len(streams)}개의 LSL stream 발견!\n")
    
    for i, stream in enumerate(streams, 1):
        print(f"[Stream {i}]")
        print(f"  Name: {stream.name()}")
        print(f"  Type: {stream.type()}")
        print(f"  Host: {stream.hostname()}")
        print(f"  Channels: {stream.channel_count()}")
        print(f"  Sample Rate: {stream.nominal_srate()}")
        print(f"  Source ID: {stream.source_id()}")
        print()
    
    # HR과 RR stream 확인
    hr_streams = [s for s in streams if 'HeartRate' in s.type() or 'HR' in s.name()]
    rr_streams = [s for s in streams if 'RRinterval' in s.type() or 'RR' in s.name()]
    
    print("\n[HR/RR Stream 확인]")
    if hr_streams:
        print(f"✅ HR stream 발견: {hr_streams[0].name()} ({hr_streams[0].type()})")
    else:
        print("❌ HR stream 없음")
    
    if rr_streams:
        print(f"✅ RR stream 발견: {rr_streams[0].name()} ({rr_streams[0].type()})")
    else:
        print("❌ RR stream 없음")
    
    if hr_streams and rr_streams:
        print("\n✅ HR과 RR stream 모두 발견됨!")
    elif hr_streams or rr_streams:
        print("\n⚠️ HR 또는 RR stream 중 하나만 발견됨")
    else:
        print("\n❌ HR과 RR stream 모두 없음")
