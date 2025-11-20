

from flask import Flask, request, jsonify
from flask_cors import CORS
from datetime import datetime, timedelta
import threading
import time

app = Flask(__name__)
CORS(app)  

PORT = 8080


rooms = {}
rooms_lock = threading.Lock()

def cleanup_old_messages():
    while True:
        time.sleep(10)  
        now = time.time()
        max_age = 30  
        
        with rooms_lock:
            rooms_to_delete = []
            for room_id, peers in rooms.items():
                peers_to_delete = []
                for peer_id, peer_data in peers.items():
                    peer_data['messages'] = [
                        msg for msg in peer_data['messages']
                        if (now - msg['timestamp']) < max_age
                    ]
                    
                    if len(peer_data['messages']) == 0 and (now - peer_data['lastPoll']) > max_age:
                        peers_to_delete.append(peer_id)
                
                for peer_id in peers_to_delete:
                    del peers[peer_id]
                
                if len(peers) == 0:
                    rooms_to_delete.append(room_id)
            
            for room_id in rooms_to_delete:
                del rooms[room_id]

cleanup_thread = threading.Thread(target=cleanup_old_messages, daemon=True)
cleanup_thread.start()

@app.route('/offer', methods=['POST'])
def handle_offer():
    """Receives an offer from a peer (usually the Android phone)"""
    try:
        data = request.json
        sdp = data.get('sdp')
        room_id = data.get('roomId')
        peer_id = data.get('peerId', 'unknown')
        
        if not sdp or not room_id:
            return jsonify({'error': 'Missing required fields: sdp, roomId'}), 400
        
        with rooms_lock:
            if room_id not in rooms:
                rooms[room_id] = {}
            
            other_peer_ids = [pid for pid in rooms[room_id].keys() if pid != peer_id]
            
            for other_peer_id in other_peer_ids:
                if other_peer_id not in rooms[room_id]:
                    rooms[room_id][other_peer_id] = {'messages': [], 'lastPoll': time.time()}
                
                rooms[room_id][other_peer_id]['messages'].append({
                    'type': 'offer',
                    'sdp': sdp,
                    'roomId': room_id,
                    'peerId': peer_id,
                    'timestamp': time.time()
                })
        
        print(f'[OFFER] Room: {room_id}, From: {peer_id}, To: {len(other_peer_ids)} peer(s)')
        
        return jsonify({
            'success': True,
            'message': 'Offer received and forwarded',
            'forwardedTo': len(other_peer_ids)
        })
    except Exception as e:
        print(f'Error handling offer: {e}')
        return jsonify({'error': str(e)}), 500

@app.route('/ice-candidate', methods=['POST'])
def handle_ice_candidate():
    """Receives an ICE candidate from a peer"""
    try:
        data = request.json
        candidate = data.get('candidate')
        sdp_mid = data.get('sdpMid', '')
        sdp_m_line_index = data.get('sdpMLineIndex', 0)
        room_id = data.get('roomId')
        peer_id = data.get('peerId', 'unknown')
        
        if not candidate or not room_id:
            return jsonify({'error': 'Missing required fields: candidate, roomId'}), 400
        
        with rooms_lock:
            if room_id not in rooms:
                rooms[room_id] = {}
            
            # Get all other peers in the room
            other_peer_ids = [pid for pid in rooms[room_id].keys() if pid != peer_id]
            
            # Forward ICE candidate to other peers
            for other_peer_id in other_peer_ids:
                if other_peer_id not in rooms[room_id]:
                    rooms[room_id][other_peer_id] = {'messages': [], 'lastPoll': time.time()}
                
                rooms[room_id][other_peer_id]['messages'].append({
                    'type': 'ice-candidate',
                    'candidate': candidate,
                    'sdpMid': sdp_mid,
                    'sdpMLineIndex': sdp_m_line_index,
                    'roomId': room_id,
                    'peerId': peer_id,
                    'timestamp': time.time()
                })
        
        print(f'[ICE] Room: {room_id}, From: {peer_id}, To: {len(other_peer_ids)} peer(s)')
        
        return jsonify({
            'success': True,
            'message': 'ICE candidate received and forwarded',
            'forwardedTo': len(other_peer_ids)
        })
    except Exception as e:
        print(f'Error handling ICE candidate: {e}')
        return jsonify({'error': str(e)}), 500

@app.route('/messages', methods=['GET'])
def poll_messages():
    """Polls for incoming messages (answer, ICE candidates)"""
    try:
        room_id = request.args.get('roomId')
        peer_id = request.args.get('peerId', 'unknown')
        
        if not room_id:
            return jsonify({'error': 'Missing required parameter: roomId'}), 400
        
        with rooms_lock:
            if room_id not in rooms:
                rooms[room_id] = {}
            if peer_id not in rooms[room_id]:
                rooms[room_id][peer_id] = {'messages': [], 'lastPoll': time.time()}
            
            peer_data = rooms[room_id][peer_id]
            peer_data['lastPoll'] = time.time()
            
            messages = peer_data['messages'].copy()
            peer_data['messages'] = []
        
        print(f'[POLL] Room: {room_id}, Peer: {peer_id}, Messages: {len(messages)}')
        
        return jsonify(messages)
    except Exception as e:
        print(f'Error polling messages: {e}')
        return jsonify({'error': str(e)}), 500

@app.route('/answer', methods=['POST'])
def handle_answer():
    """Receives an answer from a peer (usually the receiving device)"""
    try:
        data = request.json
        sdp = data.get('sdp')
        room_id = data.get('roomId')
        peer_id = data.get('peerId', 'unknown')
        
        if not sdp or not room_id:
            return jsonify({'error': 'Missing required fields: sdp, roomId'}), 400
        
        with rooms_lock:
            if room_id not in rooms:
                rooms[room_id] = {}
            
            other_peer_ids = [pid for pid in rooms[room_id].keys() if pid != peer_id]
            
            for other_peer_id in other_peer_ids:
                if other_peer_id not in rooms[room_id]:
                    rooms[room_id][other_peer_id] = {'messages': [], 'lastPoll': time.time()}
                
                rooms[room_id][other_peer_id]['messages'].append({
                    'type': 'answer',
                    'sdp': sdp,
                    'roomId': room_id,
                    'peerId': peer_id,
                    'timestamp': time.time()
                })
        
        print(f'[ANSWER] Room: {room_id}, From: {peer_id}, To: {len(other_peer_ids)} peer(s)')
        
        return jsonify({
            'success': True,
            'message': 'Answer received and forwarded',
            'forwardedTo': len(other_peer_ids)
        })
    except Exception as e:
        print(f'Error handling answer: {e}')
        return jsonify({'error': str(e)}), 500

@app.route('/status', methods=['GET'])
def get_status():
    """Check server status and room information"""
    with rooms_lock:
        room_count = len(rooms)
        total_peers = sum(len(peers) for peers in rooms.values())
        total_messages = sum(
            len(peer_data['messages'])
            for room in rooms.values()
            for peer_data in room.values()
        )
    
    return jsonify({
        'status': 'running',
        'port': PORT,
        'rooms': room_count,
        'totalPeers': total_peers,
        'totalPendingMessages': total_messages,
        'timestamp': datetime.now().isoformat()
    })

if __name__ == '__main__':
    print('=' * 40)
    print('WebRTC Signaling Server (Python)')
    print('=' * 40)
    print(f'Server running on http://localhost:{PORT}')
    print(f'Status: http://localhost:{PORT}/status')
    print('')
    print('Endpoints:')
    print('  POST /offer          - Send WebRTC offer')
    print('  POST /answer         - Send WebRTC answer')
    print('  POST /ice-candidate  - Send ICE candidate')
    print('  GET  /messages       - Poll for messages')
    print('  GET  /status         - Server status')
    print('=' * 40)
    
    app.run(host='0.0.0.0', port=PORT, debug=False)

