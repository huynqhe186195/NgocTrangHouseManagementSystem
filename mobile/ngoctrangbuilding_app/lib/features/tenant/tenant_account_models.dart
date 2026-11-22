import '../../core/network/api_exception.dart';

class TenantMobileOverview {
  const TenantMobileOverview({
    required this.currentUser,
    required this.account,
    required this.activeContract,
    required this.members,
  });

  final TenantMobileUser currentUser;
  final TenantMobileAccount account;
  final TenantMobileContract? activeContract;
  final List<TenantMobileMember> members;

  factory TenantMobileOverview.fromJson(Map<String, dynamic> json) {
    final currentUser = json['currentUser'];
    final account = json['account'];
    final members = json['members'];
    if (currentUser is! Map<String, dynamic> ||
        account is! Map<String, dynamic> ||
        members is! List) {
      throw const ApiException(
        message: 'Máy chủ trả về phạm vi tài khoản thuê không hợp lệ.',
        code: 'Common:InvalidRentalAccountOverview',
      );
    }

    return TenantMobileOverview(
      currentUser: TenantMobileUser.fromJson(currentUser),
      account: TenantMobileAccount.fromJson(account),
      activeContract: json['activeContract'] is Map<String, dynamic>
          ? TenantMobileContract.fromJson(
              json['activeContract'] as Map<String, dynamic>,
            )
          : null,
      members: members
          .whereType<Map<String, dynamic>>()
          .map(TenantMobileMember.fromJson)
          .toList(growable: false),
    );
  }
}

class TenantMobileUser {
  const TenantMobileUser({
    required this.identityUserId,
    required this.tenantId,
    required this.displayName,
    required this.accessRole,
  });

  final String identityUserId;
  final String? tenantId;
  final String? displayName;
  final String accessRole;

  factory TenantMobileUser.fromJson(Map<String, dynamic> json) {
    final identityUserId = json['identityUserId'];
    if (identityUserId is! String) {
      throw const ApiException(
        message: 'Máy chủ trả về người dùng thuê không hợp lệ.',
        code: 'Common:InvalidRentalAccountUser',
      );
    }
    return TenantMobileUser(
      identityUserId: identityUserId,
      tenantId: json['tenantId'] as String?,
      displayName: json['displayName'] as String?,
      accessRole: _enumName(json['accessRole']),
    );
  }
}

class TenantMobileAccount {
  const TenantMobileAccount({
    required this.id,
    required this.accountNumber,
    required this.displayName,
    required this.isActive,
  });

  final String id;
  final String accountNumber;
  final String displayName;
  final bool isActive;

  factory TenantMobileAccount.fromJson(Map<String, dynamic> json) {
    final id = json['id'];
    final accountNumber = json['accountNumber'];
    final displayName = json['displayName'];
    if (id is! String || accountNumber is! String || displayName is! String) {
      throw const ApiException(
        message: 'Máy chủ trả về tài khoản thuê không hợp lệ.',
        code: 'Common:InvalidRentalAccount',
      );
    }
    return TenantMobileAccount(
      id: id,
      accountNumber: accountNumber,
      displayName: displayName,
      isActive: json['isActive'] == true,
    );
  }
}

class TenantMobileContract {
  const TenantMobileContract({
    required this.id,
    required this.contractNumber,
    required this.startDate,
    required this.endDate,
    required this.room,
  });

  final String id;
  final String contractNumber;
  final DateTime startDate;
  final DateTime? endDate;
  final TenantMobileRoom room;

  factory TenantMobileContract.fromJson(Map<String, dynamic> json) {
    final id = json['id'];
    final contractNumber = json['contractNumber'];
    final startDate = json['startDate'];
    final room = json['room'];
    if (id is! String ||
        contractNumber is! String ||
        startDate is! String ||
        room is! Map<String, dynamic>) {
      throw const ApiException(
        message: 'Máy chủ trả về hợp đồng thuê không hợp lệ.',
        code: 'Common:InvalidRentalContract',
      );
    }
    final parsedStartDate = DateTime.tryParse(startDate);
    if (parsedStartDate == null) {
      throw const ApiException(
        message: 'Ngày bắt đầu hợp đồng thuê không hợp lệ.',
        code: 'Common:InvalidRentalContractDate',
      );
    }
    final rawEndDate = json['endDate'];
    final parsedEndDate = switch (rawEndDate) {
      null => null,
      String value => DateTime.tryParse(value),
      _ => null,
    };
    if (rawEndDate != null && parsedEndDate == null) {
      throw const ApiException(
        message: 'Ngày kết thúc hợp đồng thuê không hợp lệ.',
        code: 'Common:InvalidRentalContractDate',
      );
    }
    return TenantMobileContract(
      id: id,
      contractNumber: contractNumber,
      startDate: parsedStartDate,
      endDate: parsedEndDate,
      room: TenantMobileRoom.fromJson(room),
    );
  }
}

class TenantMobileRoom {
  const TenantMobileRoom({
    required this.id,
    required this.roomNumber,
    required this.name,
    required this.floorId,
    required this.floorName,
    required this.buildingId,
    required this.buildingName,
  });

  final String id;
  final String roomNumber;
  final String name;
  final String floorId;
  final String floorName;
  final String buildingId;
  final String buildingName;

  factory TenantMobileRoom.fromJson(Map<String, dynamic> json) {
    final values = <String, dynamic>{
      'id': json['id'],
      'roomNumber': json['roomNumber'],
      'name': json['name'],
      'floorId': json['floorId'],
      'floorName': json['floorName'],
      'buildingId': json['buildingId'],
      'buildingName': json['buildingName'],
    };
    if (values.values.any((value) => value is! String)) {
      throw const ApiException(
        message: 'Máy chủ trả về phòng thuê không hợp lệ.',
        code: 'Common:InvalidRentalRoom',
      );
    }
    return TenantMobileRoom(
      id: values['id'] as String,
      roomNumber: values['roomNumber'] as String,
      name: values['name'] as String,
      floorId: values['floorId'] as String,
      floorName: values['floorName'] as String,
      buildingId: values['buildingId'] as String,
      buildingName: values['buildingName'] as String,
    );
  }
}

class TenantMobileMember {
  const TenantMobileMember({
    required this.tenantId,
    required this.displayName,
    required this.role,
  });

  final String tenantId;
  final String displayName;
  final String role;

  factory TenantMobileMember.fromJson(Map<String, dynamic> json) {
    final tenantId = json['tenantId'];
    final displayName = json['displayName'];
    final role = json['role'];
    if (tenantId is! String || displayName is! String || role is! String) {
      throw const ApiException(
        message: 'Máy chủ trả về thành viên thuê không hợp lệ.',
        code: 'Common:InvalidRentalMember',
      );
    }
    return TenantMobileMember(
      tenantId: tenantId,
      displayName: displayName,
      role: role,
    );
  }
}

String _enumName(dynamic value) {
  if (value is String) return value;
  if (value is num) {
    return switch (value.toInt()) {
      1 => 'PrimaryTenant',
      2 => 'MemberTenant',
      3 => 'AuthorizedRepresentative',
      4 => 'Viewer',
      _ => 'Unknown',
    };
  }
  return 'Unknown';
}
